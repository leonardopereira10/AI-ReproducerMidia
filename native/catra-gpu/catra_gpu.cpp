// catra_gpu.cpp — CATRA GPU native bridge implementation.
//
// Lifecycle + capability queries are wired; the RIFE interpolation backend is
// implemented (ST-13, see interp_rife.cpp); upscale (ST-14) and encode (ST-16)
// still return CATRA_ERR_NOT_IMPL. The log callback is stored and used to
// surface diagnostics to the C# layer.
//
// EXCEPTION BARRIER: every entry point that reaches a backend is wrapped in
// GuardCabi/GuardCabiVoid so no C++ exception can unwind across the extern "C"
// / P-Invoke boundary (undefined behaviour). Stray exceptions (std::bad_alloc
// from tensor buffers, mutex failures, ...) surface as CATRA_ERR_UNKNOWN.

#define CATRA_GPU_BUILDING // export the CATRA_API symbols from this TU
#include "catra_gpu.h"
#include "interp_rife.h"
#include "upscale_fsr1.h"
#include "upscale_fsr4.h"

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <memory>
#include <mutex>
#include <unordered_map>

#include <d3d11.h>
#include <wrl/client.h>

namespace {

// --- Global bridge state ---------------------------------------------------

std::atomic<bool> g_initialized{false};

// The log sink is read from any thread, so guard the pointer itself. The
// callback is invoked outside the lock to avoid deadlocks if the callee ever
// blocks.
std::mutex g_logMutex;
catra_log_callback g_logCallback = nullptr;

// Formats and forwards a diagnostic line to the managed sink (if installed).
void log_msg(int level, const char* fmt, ...)
{
    char buffer[512];

    va_list args;
    va_start(args, fmt);
    std::vsnprintf(buffer, sizeof(buffer), fmt, args);
    va_end(args);

    buffer[sizeof(buffer) - 1] = '\0';

    catra_log_callback cb = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_logMutex);
        cb = g_logCallback;
    }
    if (cb != nullptr)
    {
        cb(buffer, level);
    }
}

// The bridge's shared D3D11 device + immediate context, captured by
// catra_init and borrowed by the interp backend (ST-13). Guarded by the
// init/shutdown ordering: backends are torn down in catra_shutdown before
// these are released.
Microsoft::WRL::ComPtr<ID3D11Device> g_device;
Microsoft::WRL::ComPtr<ID3D11DeviceContext> g_deviceContext;

// Effective method (CATRA_UPSCALE_*) of the most recently created upscale
// context; surfaced by catra_get_upscale_mode. OFF until the first create.
std::atomic<int> g_currentUpscaleMode{CATRA_UPSCALE_OFF};

// C ABI exception barrier. A C++ exception escaping an extern "C" function
// into the managed P/Invoke frame is UB, so every fallible entry point routes
// through this wrapper. The diagnostic log is itself guarded because logging
// takes a mutex and could (in theory) throw while we are already unwinding.
template <typename Fn>
int GuardCabi(Fn&& fn) noexcept
{
    try
    {
        return fn();
    }
    catch (...)
    {
        try
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra-gpu: unhandled C++ exception at C ABI boundary -> CATRA_ERR_UNKNOWN");
        }
        catch (...)
        {
        }
        return CATRA_ERR_UNKNOWN;
    }
}

// Void variant for the noexcept-shaped entry points (shutdown / destroy).
template <typename Fn>
void GuardCabiVoid(Fn&& fn) noexcept
{
    try
    {
        fn();
    }
    catch (...)
    {
        try
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra-gpu: unhandled C++ exception at C ABI boundary (swallowed)");
        }
        catch (...)
        {
        }
    }
}

} // namespace

namespace catra {

// Backend log shim (declared in interp_rife.h's companion): forwards to the
// same sink as the C ABI diagnostics. Defined here so every backend TU can
// emit through the managed callback without duplicating the plumbing.
void BackendLog(int level, const char* fmt, ...)
{
    char buffer[512];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(buffer, sizeof(buffer), fmt, args);
    va_end(args);
    buffer[sizeof(buffer) - 1] = '\0';
    log_msg(level, "%s", buffer);
}

} // namespace catra

namespace {

// --- Upscale context registry (ST-14) --------------------------------------
//
// Mirrors the RIFE registry (interp_rife.cpp): dense non-negative int handles,
// mutex-guarded map, heavy work outside the lock. A context owns exactly one
// backend (passthrough needs none; FSR 1 / FSR 4 each own their pipeline).

struct UpscaleContext
{
    int method = CATRA_UPSCALE_OFF; // effective method after any FSR4->FSR1 downgrade
    int srcW = 0;
    int srcH = 0;
    int dstW = 0;
    int dstH = 0;
    catra::UpscaleQualityMode quality = catra::UpscaleQualityMode::Quality;

    ID3D11Device* device = nullptr;               // borrowed (bridge-owned)
    ID3D11DeviceContext* deviceContext = nullptr; // borrowed (passthrough copy)

    std::unique_ptr<catra::Fsr1Upscaler> fsr1;
    std::unique_ptr<catra::Fsr4Upscaler> fsr4;
};

std::mutex g_upscaleMutex;
std::unordered_map<int, std::unique_ptr<UpscaleContext>> g_upscaleContexts;
int g_nextUpscaleHandle = 0;

int RegisterUpscale(std::unique_ptr<UpscaleContext> ctx)
{
    std::lock_guard<std::mutex> lock(g_upscaleMutex);
    int handle = g_nextUpscaleHandle++;
    g_upscaleContexts.emplace(handle, std::move(ctx));
    return handle;
}

UpscaleContext* LookupUpscale(int handle)
{
    std::lock_guard<std::mutex> lock(g_upscaleMutex);
    auto it = g_upscaleContexts.find(handle);
    return it == g_upscaleContexts.end() ? nullptr : it->second.get();
}

void DestroyUpscale(int handle)
{
    std::lock_guard<std::mutex> lock(g_upscaleMutex);
    g_upscaleContexts.erase(handle); // unique_ptr frees the backend pipeline
}

// Called from catra_shutdown BEFORE the bridge releases its D3D11 device, which
// the contexts borrow.
void DestroyAllUpscale()
{
    std::lock_guard<std::mutex> lock(g_upscaleMutex);
    g_upscaleContexts.clear();
}

// Passthrough (method=0): copies the source D3D11 texture into a fresh,
// shader-bindable D3D11 texture and returns it (refcount 1, caller owns).
// CopyResource requires matching dimensions/format, so the source is validated
// against the context's src size first.
int UpscalePassthrough(UpscaleContext* ctx, ID3D11Texture2D* src,
                       ID3D11Texture2D** outDst)
{
    D3D11_TEXTURE2D_DESC desc = {};
    src->GetDesc(&desc);
    if (static_cast<int>(desc.Width) != ctx->srcW ||
        static_cast<int>(desc.Height) != ctx->srcH)
    {
        log_msg(CATRA_LOG_ERROR, "upscale: passthrough src %ux%u != context %dx%d",
                desc.Width, desc.Height, ctx->srcW, ctx->srcH);
        return CATRA_ERR_INVALID_ARG;
    }

    // Destination: same geometry, GPU-default + shader-bindable (CopyResource
    // tolerates differing usage/bind/CPU-access flags).
    D3D11_TEXTURE2D_DESC dstDesc = desc;
    dstDesc.Usage = D3D11_USAGE_DEFAULT;
    dstDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    dstDesc.CPUAccessFlags = 0;
    dstDesc.MiscFlags = 0;

    Microsoft::WRL::ComPtr<ID3D11Texture2D> dst;
    HRESULT hr = ctx->device->CreateTexture2D(&dstDesc, nullptr, dst.GetAddressOf());
    if (FAILED(hr))
    {
        log_msg(CATRA_LOG_ERROR, "upscale: passthrough CreateTexture2D hr=0x%08lX",
                static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    ctx->deviceContext->CopyResource(dst.Get(), src);

    *outDst = dst.Detach(); // caller owns the reference
    return CATRA_OK;
}

} // namespace

// ===========================================================================
// Lifecycle
// ===========================================================================

int catra_init(void* d3d11_device)
{
    return GuardCabi([&]() -> int {
        if (d3d11_device == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_init: null d3d11_device");
            return CATRA_ERR_INVALID_ARG;
        }

        if (g_initialized.exchange(true))
        {
            log_msg(CATRA_LOG_WARN, "catra_init: already initialized");
            return CATRA_OK;
        }

        // Retain the caller's device + immediate context for the backends
        // (ST-13 interp reads/writes D3D11 textures through them).
        ID3D11Device* device = static_cast<ID3D11Device*>(d3d11_device);
        g_device = device;
        g_device->GetImmediateContext(g_deviceContext.GetAddressOf());

        // ST-15: derive the DXGI adapter from d3d11_device and create the
        // D3D12 device + command queue / allocator lists here.
        log_msg(CATRA_LOG_INFO, "catra_init: bridge initialized");
        return CATRA_OK;
    });
}

void catra_shutdown(void)
{
    GuardCabiVoid([&]() {
        if (!g_initialized.exchange(false))
        {
            return; // idempotent
        }

        // Release every backend context BEFORE dropping the D3D11 device they
        // borrow, then let go of the device/context.
        catra::InterpRifeDestroyAll();
        DestroyAllUpscale();
        g_deviceContext.Reset();
        g_device.Reset();

        // ST-15: flush + release the D3D12 command queue and device here.
        log_msg(CATRA_LOG_INFO, "catra_shutdown: bridge shut down");
    });
}

int catra_get_upscale_mode(void)
{
    // Reports the effective method of the most recently created upscale context
    // (CATRA_UPSCALE_OFF when none exists yet). "Current" is well-defined for
    // the offline pipeline, which runs one upscale job at a time.
    return g_currentUpscaleMode.load();
}

int catra_is_fsr4_available(void)
{
    // Probes the active adapter (AMD + RDNA 4 + a throw-away FidelityFX context).
    // Returns 0 before catra_init (no active adapter) and whenever the bridge
    // was built without the FSR 4 SDK or the adapter is not RDNA 4.
    return GuardCabi([&]() -> int {
        return catra::Fsr4IsAvailable(g_device.Get()) ? 1 : 0;
    });
}

int catra_get_interp_method(void)
{
    // ST-13: RIFE is the compiled-in interpolation backend. When the bridge was
    // built without ONNX Runtime the backend reports unavailable -> NONE.
    return GuardCabi([&]() -> int {
        return catra::InterpRifeIsCompiled() ? CATRA_INTERP_RIFE : CATRA_INTERP_NONE;
    });
}

void catra_set_log_callback(catra_log_callback cb)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    g_logCallback = cb;
}

// ===========================================================================
// Frame interpolation — RIFE v4 backend (ST-13)
// ===========================================================================

int catra_interp_create(int src_w, int src_h,
                        double src_fps, double target_fps,
                        int method, int* out_ctx)
{
    return GuardCabi([&]() -> int {
        if (!g_initialized.load())
        {
            log_msg(CATRA_LOG_ERROR, "catra_interp_create: bridge not initialized");
            if (out_ctx != nullptr)
            {
                *out_ctx = -1;
            }
            return CATRA_ERR_INIT;
        }
        return catra::InterpRifeCreate(g_device.Get(), g_deviceContext.Get(),
                                       src_w, src_h, src_fps, target_fps,
                                       method, out_ctx);
    });
}

int catra_interp_process(int ctx,
                         void* frame_a, void* frame_b,
                         void** out_frames, int* out_count)
{
    // InterpRifeProcess releases any partial output set before it reports
    // failure or rethrows; the guard below converts a rethrown non-ORT
    // exception (e.g. std::bad_alloc) into CATRA_ERR_UNKNOWN so nothing ever
    // unwinds into the P/Invoke frame. *out_frames stays null on failure.
    return GuardCabi([&]() -> int {
        return catra::InterpRifeProcess(ctx, frame_a, frame_b, out_frames, out_count);
    });
}

void catra_interp_destroy(int ctx)
{
    GuardCabiVoid([&]() {
        catra::InterpRifeDestroy(ctx);
    });
}

// ===========================================================================
// Upscale — FSR 4 (FidelityFX SDK) with FSR 1 (EASU) fallback (ST-14)
// ===========================================================================

int catra_upscale_create(int src_w, int src_h,
                         int dst_w, int dst_h,
                         int method, int* out_ctx)
{
    return GuardCabi([&]() -> int {
        if (out_ctx != nullptr)
        {
            *out_ctx = -1;
        }
        if (!g_initialized.load())
        {
            log_msg(CATRA_LOG_ERROR, "catra_upscale_create: bridge not initialized");
            return CATRA_ERR_INIT;
        }
        if (src_w <= 0 || src_h <= 0 || dst_w <= 0 || dst_h <= 0 || out_ctx == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_upscale_create: invalid args (%dx%d -> %dx%d)",
                    src_w, src_h, dst_w, dst_h);
            return CATRA_ERR_INVALID_ARG;
        }
        if (method != CATRA_UPSCALE_OFF && method != CATRA_UPSCALE_FSR1 &&
            method != CATRA_UPSCALE_FSR4)
        {
            log_msg(CATRA_LOG_WARN,
                    "catra_upscale_create: unknown method %d -> passthrough", method);
        }

        // Resolve the effective method: FSR 4 downgrades to FSR 1 when the SDK
        // is absent or the adapter is not RDNA 4 (RN-07 / risk mitigation). The
        // (potentially expensive) availability probe runs ONLY when FSR 4 was
        // actually requested; for passthrough/FSR 1 we pass false, which
        // ResolveUpscaleMethod ignores for those methods.
        const bool fsr4Available = (method == CATRA_UPSCALE_FSR4) &&
                                   catra::Fsr4IsAvailable(g_device.Get());
        const int effective = catra::ResolveUpscaleMethod(method, fsr4Available);
        if (method == CATRA_UPSCALE_FSR4 && effective == CATRA_UPSCALE_FSR1)
        {
            log_msg(CATRA_LOG_WARN,
                    "catra_upscale_create: FSR 4 unavailable -> downgrade to FSR 1");
        }

        auto ctx = std::make_unique<UpscaleContext>();
        ctx->method = effective;
        ctx->srcW = src_w;
        ctx->srcH = src_h;
        ctx->dstW = dst_w;
        ctx->dstH = dst_h;
        ctx->quality = catra::SelectQualityMode(src_w, src_h, dst_w, dst_h);
        ctx->device = g_device.Get();
        ctx->deviceContext = g_deviceContext.Get();

        int rc = CATRA_OK;
        if (effective == CATRA_UPSCALE_FSR1)
        {
            rc = catra::Fsr1Upscaler::Create(g_device.Get(), src_w, src_h, dst_w, dst_h,
                                             ctx->fsr1);
        }
        else if (effective == CATRA_UPSCALE_FSR4)
        {
            rc = catra::Fsr4Upscaler::Create(g_device.Get(), src_w, src_h, dst_w, dst_h,
                                             ctx->quality, ctx->fsr4);
        }
        // effective == CATRA_UPSCALE_OFF: passthrough, no backend needed.

        if (rc != CATRA_OK)
        {
            // Backend construction failed; nothing was registered, so the
            // unique_ptr simply drops any partial state on scope exit (RAII).
            log_msg(CATRA_LOG_ERROR,
                    "catra_upscale_create: backend create failed rc=%d (method=%d)",
                    rc, effective);
            return rc;
        }

        const char* name = effective == CATRA_UPSCALE_FSR4 ? "FSR 4"
                         : effective == CATRA_UPSCALE_FSR1 ? "FSR 1"
                                                           : "passthrough";
        log_msg(CATRA_LOG_INFO,
                "catra_upscale_create: %s %dx%d -> %dx%d (quality=%d)",
                name, src_w, src_h, dst_w, dst_h, static_cast<int>(ctx->quality));

        g_currentUpscaleMode.store(effective);
        const int handle = RegisterUpscale(std::move(ctx));
        *out_ctx = handle;
        return CATRA_OK;
    });
}

int catra_upscale_process(int ctx, void* src_texture, void** dst_texture)
{
    // Every failure path leaves *dst_texture null; backends release any partial
    // output before returning, and the C ABI guard converts a stray exception
    // into CATRA_ERR_UNKNOWN so nothing unwinds into the P/Invoke frame.
    return GuardCabi([&]() -> int {
        if (dst_texture != nullptr)
        {
            *dst_texture = nullptr;
        }

        UpscaleContext* c = LookupUpscale(ctx);
        if (c == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_upscale_process: unknown context %d", ctx);
            return CATRA_ERR_CONTEXT;
        }
        if (src_texture == nullptr || dst_texture == nullptr)
        {
            return CATRA_ERR_INVALID_ARG;
        }

        ID3D11Texture2D* src = static_cast<ID3D11Texture2D*>(src_texture);

        if (c->method == CATRA_UPSCALE_OFF)
        {
            ID3D11Texture2D* dst = nullptr;
            int rc = UpscalePassthrough(c, src, &dst);
            if (rc != CATRA_OK)
            {
                return rc;
            }
            *dst_texture = dst; // caller owns the reference
            return CATRA_OK;
        }

        ID3D12Resource* dst12 = nullptr;
        int rc = (c->method == CATRA_UPSCALE_FSR4)
                     ? c->fsr4->Process(src, &dst12)
                     : c->fsr1->Process(src, &dst12);
        if (rc != CATRA_OK)
        {
            // Backends guarantee dst12 is null on failure; release defensively.
            if (dst12 != nullptr)
            {
                dst12->Release();
            }
            return rc;
        }
        *dst_texture = dst12; // caller owns the reference
        return CATRA_OK;
    });
}

void catra_upscale_destroy(int ctx)
{
    GuardCabiVoid([&]() {
        DestroyUpscale(ctx); // no-op for an unknown handle
    });
}

// ===========================================================================
// Encode — stubs (ST-16)
// ===========================================================================

int catra_encode_create(int width, int height,
                        int bitrate_kbps, double fps, int* out_ctx)
{
    (void)width; (void)height; (void)bitrate_kbps; (void)fps;
    if (out_ctx != nullptr)
    {
        *out_ctx = -1;
    }
    return CATRA_ERR_NOT_IMPL;
}

int catra_encode_frame(int ctx, void* texture, uint8_t** out_buf, int* out_size)
{
    (void)ctx; (void)texture;
    if (out_buf != nullptr)
    {
        *out_buf = nullptr;
    }
    if (out_size != nullptr)
    {
        *out_size = 0;
    }
    return CATRA_ERR_NOT_IMPL;
}

int catra_encode_flush(int ctx, uint8_t** out_buf, int* out_size)
{
    (void)ctx;
    if (out_buf != nullptr)
    {
        *out_buf = nullptr;
    }
    if (out_size != nullptr)
    {
        *out_size = 0;
    }
    return CATRA_ERR_NOT_IMPL;
}

void catra_encode_destroy(int ctx)
{
    (void)ctx;
}

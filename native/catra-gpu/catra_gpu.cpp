// catra_gpu.cpp — CATRA GPU native bridge implementation.
//
// Lifecycle + capability queries are wired; the RIFE interpolation backend is
// implemented (ST-13, see interp_rife.cpp); upscale (ST-14) runs on the shared
// D3D12 device created at init by the D3D11<->DX12 interop (ST-15, see
// d3d_interop.cpp); encode (ST-16) still returns CATRA_ERR_NOT_IMPL. The log
// callback is stored and used to surface diagnostics to the C# layer.
//
// EXCEPTION BARRIER: every entry point that reaches a backend is wrapped in
// GuardCabi/GuardCabiVoid so no C++ exception can unwind across the extern "C"
// / P-Invoke boundary (undefined behaviour). Stray exceptions (std::bad_alloc
// from tensor buffers, mutex failures, ...) surface as CATRA_ERR_UNKNOWN.

#ifndef CATRA_GPU_BUILDING
#define CATRA_GPU_BUILDING // export the CATRA_API symbols from this TU
#endif
#include "catra_gpu.h"
#include "d3d_interop.h"
#include "encode_amf.h"
#include "ffx_runtime.h"
#include "interp_rife.h"
#include "upscale_fsr1.h"
#include "upscale_fsr4.h"
#include "nv12_to_bgra_shader.h"

#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h> // IsWindow (catra_fg_create HWND validation)

#include <d3d11.h>
#include <d3d12.h>
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

// ST-15: the D3D12 side of the bridge, created by catra::interop_init on the
// SAME adapter as g_device (a hard requirement for NT shared-handle interop).
// The DX12 backends (FSR 1 / FSR 4) receive this very device through
// catra::CreateD3D12Device, so every dispatch and every shared texture lives
// on one device/LUID. Released in catra_shutdown AFTER catra::interop_shutdown
// (the pool textures reference both devices).
Microsoft::WRL::ComPtr<ID3D12Device> g_d3d12Device;
Microsoft::WRL::ComPtr<ID3D12CommandQueue> g_d3d12Queue;

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
//
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

// Forward declarations
struct UpscaleContext;
int UpscalePassthrough(UpscaleContext* ctx, ID3D11Texture2D* src, ID3D11Texture2D** outDst);

// Async upscale operation state.
struct AsyncUpscaleOp
{
    int ticket = 0;
    ID3D11Texture2D* srcTexture = nullptr; // caller-owned until processed
    ID3D12Resource* dstTexture = nullptr;  // set when complete
    bool completed = false;
    bool failed = false;
    int errorCode = CATRA_OK;
};

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
    ID3D12Device* device12 = nullptr;             // borrowed (bridge-owned, for async share)

    std::unique_ptr<catra::Fsr1Upscaler> fsr1;
    std::unique_ptr<catra::Fsr4Upscaler> fsr4;

    // CRITICAL: Protects fsr1/fsr4 Process() calls — these backends are NOT thread-safe
    // (they reuse command allocators / descriptor heaps internally).
    std::mutex processMutex;

    // Async upscale queue
    std::mutex asyncMutex;
    std::condition_variable asyncCv;
    std::deque<AsyncUpscaleOp> asyncQueue;
    std::deque<AsyncUpscaleOp> asyncResults;
    int nextTicket = 1;
    std::thread asyncWorker;
    std::atomic<bool> shutdownRequested{false};
    static constexpr int kMaxQueueDepth = 64;
};

std::mutex g_upscaleMutex;
std::unordered_map<int, std::unique_ptr<UpscaleContext>> g_upscaleContexts;
int g_nextUpscaleHandle = 0;

// Async worker thread function for upscale context.
// Processes queued upscale operations without blocking the caller.
// OWNERSHIP: The worker owns the source texture after submit (no AddRef).
// It releases the source after processing and stores the result.
// The pipeline C# side must NOT release the source texture after poll.
void AsyncUpscaleWorker(UpscaleContext* ctx)
{
    while (true)
    {
        AsyncUpscaleOp op;
        {
            std::unique_lock<std::mutex> lock(ctx->asyncMutex);
            ctx->asyncCv.wait(lock, [ctx] {
                return ctx->shutdownRequested.load() || !ctx->asyncQueue.empty();
            });

            if (ctx->shutdownRequested.load() && ctx->asyncQueue.empty())
            {
                return;
            }

            op = std::move(ctx->asyncQueue.front());
            ctx->asyncQueue.pop_front();
        }

        // Process the upscale operation outside the lock
        ID3D12Resource* dst12 = nullptr;
        int rc = CATRA_OK;

        if (ctx->method == CATRA_UPSCALE_OFF)
        {
            // Passthrough: need to convert D3D11 src to D3D12 via ShareTexture
            ID3D11Texture2D* dst11 = nullptr;
            rc = UpscalePassthrough(ctx, op.srcTexture, &dst11);
            if (rc == CATRA_OK && dst11 != nullptr)
            {
                // Share to D3D12
                HRESULT hr = catra::ShareTexture(ctx->device, ctx->device12, dst11, &dst12);
                dst11->Release();
                if (FAILED(hr))
                {
                    rc = CATRA_ERR_DEVICE;
                }
            }
        }
        else if (ctx->method == CATRA_UPSCALE_FSR4 && ctx->fsr4)
        {
            // CRITICAL: fsr4->Process() is NOT thread-safe — must hold processMutex
            std::lock_guard<std::mutex> procLock(ctx->processMutex);
            rc = ctx->fsr4->Process(op.srcTexture, &dst12);
        }
        else if (ctx->fsr1)
        {
            // CRITICAL: fsr1->Process() is NOT thread-safe — must hold processMutex
            std::lock_guard<std::mutex> procLock(ctx->processMutex);
            rc = ctx->fsr1->Process(op.srcTexture, &dst12);
        }
        else
        {
            rc = CATRA_ERR_NOT_IMPL;
        }

        // Release source texture (worker owns it after submit, no AddRef)
        if (op.srcTexture != nullptr)
        {
            op.srcTexture->Release();
            op.srcTexture = nullptr;
        }

        // Store result
        op.dstTexture = dst12;
        op.completed = true;
        op.failed = (rc != CATRA_OK);
        op.errorCode = rc;

        {
            std::lock_guard<std::mutex> lock(ctx->asyncMutex);
            ctx->asyncResults.push_back(std::move(op));
        }
        ctx->asyncCv.notify_one();
    }
}

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
    std::unique_ptr<UpscaleContext> ctx;
    {
        std::lock_guard<std::mutex> lock(g_upscaleMutex);
        auto it = g_upscaleContexts.find(handle);
        if (it == g_upscaleContexts.end())
        {
            return;
        }
        ctx = std::move(it->second);
        g_upscaleContexts.erase(it);
    }

    // Stop async worker if running
    if (ctx->asyncWorker.joinable())
    {
        {
            std::lock_guard<std::mutex> lock(ctx->asyncMutex);
            ctx->shutdownRequested.store(true);
        }
        ctx->asyncCv.notify_all();
        ctx->asyncWorker.join();
    }

    // Release any pending async operations
    for (auto& op : ctx->asyncQueue)
    {
        if (op.srcTexture != nullptr)
        {
            op.srcTexture->Release();
        }
    }
    for (auto& op : ctx->asyncResults)
    {
        if (op.dstTexture != nullptr)
        {
            op.dstTexture->Release();
        }
    }
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

// --- Encode context registry (ST-16) ---------------------------------------
//
// Mirrors the upscale/RIFE registries: dense non-negative int handles,
// mutex-guarded map, heavy work outside the lock. A context owns exactly one
// catra::AmfEncoder (the AMF factory/context/component + staging buffer).

struct EncodeContext
{
    int width = 0;
    int height = 0;
    int bitrateKbps = 0;
    double fps = 0.0;
    std::unique_ptr<catra::AmfEncoder> encoder;
};

std::mutex g_encodeMutex;
std::unordered_map<int, std::unique_ptr<EncodeContext>> g_encodeContexts;
int g_nextEncodeHandle = 0;

int RegisterEncode(std::unique_ptr<EncodeContext> ctx)
{
    std::lock_guard<std::mutex> lock(g_encodeMutex);
    int handle = g_nextEncodeHandle++;
    g_encodeContexts.emplace(handle, std::move(ctx));
    return handle;
}

EncodeContext* LookupEncode(int handle)
{
    std::lock_guard<std::mutex> lock(g_encodeMutex);
    auto it = g_encodeContexts.find(handle);
    return it == g_encodeContexts.end() ? nullptr : it->second.get();
}

void DestroyEncode(int handle)
{
    fprintf(stderr, "DestroyEncode: ENTER handle=%d\n", handle);
    std::lock_guard<std::mutex> lock(g_encodeMutex);
    auto it = g_encodeContexts.find(handle);
    if (it == g_encodeContexts.end())
    {
        fprintf(stderr, "DestroyEncode: unknown handle %d\n", handle);
        log_msg(CATRA_LOG_WARN, "DestroyEncode: unknown handle %d", handle);
        return;
    }
    fprintf(stderr, "DestroyEncode: erasing handle %d (unique_ptr will free AMF)\n", handle);
    g_encodeContexts.erase(it); // unique_ptr frees the AMF encoder (RAII)
    fprintf(stderr, "DestroyEncode: handle %d erased OK\n", handle);
}

// Called from catra_shutdown BEFORE the bridge releases its D3D12 device, which
// the AMF contexts borrow (AMFContext::InitDX12 AddRef'd it).
void DestroyAllEncode()
{
    std::lock_guard<std::mutex> lock(g_encodeMutex);
    g_encodeContexts.clear();
}

// --- FG playback context registry (SPRINT_04 subtask 04) -------------------
//
// Mirrors the upscale/encode registries: dense non-negative int handles,
// mutex-guarded map, heavy work outside the lock. A handle owns exactly one
// catra::FgRenderer (FG swapchain context + FG context + letterbox pipeline).
// Lookups hand out a raw pointer; heavy work (present/resize) runs OUTSIDE
// the lock so the FFX callbacks (runtime threads) never contend with it.

std::mutex g_fgMutex;
std::unordered_map<int, std::unique_ptr<catra::FgRenderer>> g_fgContexts;
int g_nextFgHandle = 0;

int RegisterFg(std::unique_ptr<catra::FgRenderer> renderer)
{
    std::lock_guard<std::mutex> lock(g_fgMutex);
    const int handle = g_nextFgHandle++;
    g_fgContexts.emplace(handle, std::move(renderer));
    return handle;
}

catra::FgRenderer* LookupFg(int handle)
{
    std::lock_guard<std::mutex> lock(g_fgMutex);
    auto it = g_fgContexts.find(handle);
    return it == g_fgContexts.end() ? nullptr : it->second.get();
}

void DestroyFg(int handle)
{
    std::lock_guard<std::mutex> lock(g_fgMutex);
    g_fgContexts.erase(handle); // unique_ptr runs the full FFX teardown (RAII)
}

// Called from catra_shutdown BEFORE interop_shutdown / device release: the
// renderers borrow the interop D3D12 device + DIRECT queue.
void DestroyAllFg()
{
    std::lock_guard<std::mutex> lock(g_fgMutex);
    g_fgContexts.clear();
}

} // namespace

namespace catra {

// Test hook used ONLY by tools/fg_smoke_test.cpp: installs the present
// observer on a live FG context (counts rendered + generated presents).
// No-op for an unknown handle. Not part of the C ABI, never exported.
void FgSetPresentObserver(int ctx, FgPresentObserverFn cb, void* user)
{
    catra::FgRenderer* r = LookupFg(ctx);
    if (r != nullptr)
    {
        r->SetPresentObserver(cb, user);
    }
}

} // namespace catra

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

        // ST-15: bring up the D3D12 side on the same adapter — shared device,
        // DIRECT command queue, command allocator/list + fence (d3d_interop).
        // Soft-fail by design: when D3D12 is unavailable the D3D11-only paths
        // (RIFE interp, passthrough upscale) remain fully usable and the DX12
        // backends surface CATRA_ERR_DEVICE at use time (RN-07 degradation).
        ID3D12Device* device12 = nullptr;
        ID3D12CommandQueue* queue12 = nullptr;
        const int irc = catra::interop_init(device, &device12, &queue12);
        if (irc == CATRA_OK)
        {
            g_d3d12Device.Attach(device12);
            g_d3d12Queue.Attach(queue12);
            log_msg(CATRA_LOG_INFO,
                    "catra_init: D3D11<->DX12 interop ready (shared adapter)");
        }
        else
        {
            log_msg(CATRA_LOG_WARN,
                    "catra_init: interop_init rc=%d -> DX12 path disabled "
                    "(D3D11 paths unaffected)", irc);
        }

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
        DestroyAllEncode(); // ST-16: AMF contexts borrow the D3D12 device
        DestroyAllFg(); // subtask 04: FG contexts borrow device12 + queue12

        // ST-23: release the cached NV12→BGRA compute shader + output texture.
        catra::nv12_bgra_shutdown();

        // ST-15: tear down the interop (pool textures reference BOTH devices)
        // before releasing the bridge's device refs. Idempotent.
        catra::interop_shutdown();
        g_d3d12Queue.Reset();
        g_d3d12Device.Reset();
        g_deviceContext.Reset();
        g_device.Reset();

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
    // Probes the active adapter with RUNTIME-LOAD semantics (SPRINT_04 subtask
    // 02 — no build-time gate): FFX loader + 5 ffx exports, the 8 FFX runtime
    // DLLs next to catra-gpu.dll and a throw-away FFX upscale context on the
    // adapter (the FFX runtime picks FSR 4 ML on RDNA 4, FSR 3.1 elsewhere).
    // Returns 0 before catra_init (no active adapter) and on any probe failure.
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
// Resource ownership / release helpers
// ===========================================================================
//
// Matched deallocators for the caller-owned resources the bridge hands back
// (upscale destinations, interpolation intermediate arrays + textures). Both
// are null-safe no-ops and route through GuardCabi so no C++ exception can
// unwind into the P/Invoke frame.

int catra_release_texture(void* texture)
{
    return GuardCabi([&]() -> int {
        if (texture == nullptr)
        {
            return CATRA_OK; // null-safe no-op
        }

        // ID3D11Texture2D and ID3D12Resource both derive from IUnknown, so the
        // refcount release goes through the common base regardless of which API
        // produced the texture (passthrough/interp -> D3D11, FSR 1/4 -> D3D12).
        static_cast<IUnknown*>(texture)->Release();
        return CATRA_OK;
    });
}

int catra_free(void* ptr)
{
    return GuardCabi([&]() -> int {
        if (ptr == nullptr)
        {
            return CATRA_OK; // null-safe no-op
        }

        // Matches the `new void*[N]` allocation in interp_rife.cpp (CRT heap).
        // The contained textures must already have been released by the caller
        // (catra_release_texture); this only frees the pointer array itself.
        delete[] static_cast<void**>(ptr);
        return CATRA_OK;
    });
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

        // Resolve the effective method: FSR 4 downgrades to FSR 1 when the
        // FFX runtime is unavailable (loader/8 DLLs missing or the throw-away
        // context probe fails on the adapter — RN-07 / risk mitigation). The
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
        ctx->device12 = g_d3d12Device.Get();

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
        // CRITICAL: fsr->Process() is NOT thread-safe — must hold processMutex
        std::lock_guard<std::mutex> procLock(c->processMutex);
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

int catra_upscale_reset(int ctx)
{
    // Scene-cut signal (SPRINT_04 subtask 02, additive — D-PO-1): the next
    // FSR 4/3.1 dispatch on this context runs with reset=true, flushing the
    // temporal accumulation. FSR 1 / passthrough have no temporal state ->
    // no-op CATRA_OK. Unknown handle -> CATRA_ERR_CONTEXT.
    return GuardCabi([&]() -> int {
        UpscaleContext* c = LookupUpscale(ctx);
        if (c == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_upscale_reset: unknown context %d", ctx);
            return CATRA_ERR_CONTEXT;
        }
        if (c->method == CATRA_UPSCALE_FSR4 && c->fsr4 != nullptr)
        {
            return c->fsr4->RequestSceneCut();
        }
        return CATRA_OK; // fsr1 / passthrough: nothing to reset
    });
}

void catra_upscale_destroy(int ctx)
{
    GuardCabiVoid([&]() {
        DestroyUpscale(ctx); // no-op for an unknown handle
    });
}

// ===========================================================================
// Async Upscale API
// ===========================================================================

int catra_upscale_submit_async(int ctx_handle, void* src_texture)
{
    return GuardCabi([&]() -> int {
        UpscaleContext* ctx = LookupUpscale(ctx_handle);
        if (ctx == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_upscale_submit_async: unknown context %d", ctx_handle);
            return CATRA_ERR_CONTEXT;
        }
        if (src_texture == nullptr)
        {
            return CATRA_ERR_INVALID_ARG;
        }

        // Start worker thread on first use
        {
            std::lock_guard<std::mutex> lock(ctx->asyncMutex);
            if (!ctx->asyncWorker.joinable())
            {
                ctx->shutdownRequested.store(false);
                ctx->asyncWorker = std::thread(AsyncUpscaleWorker, ctx);
            }
        }

        // NO AddRef - ownership transfers to the worker thread directly.
        // The worker will Release() the texture after processing.
        // The caller (pipeline C#) must NOT release the texture after submit.
        ID3D11Texture2D* src = static_cast<ID3D11Texture2D*>(src_texture);

        int ticket;
        {
            std::unique_lock<std::mutex> lock(ctx->asyncMutex);

            // Wait if queue is full (backpressure)
            ctx->asyncCv.wait(lock, [ctx] {
                return static_cast<int>(ctx->asyncQueue.size()) < UpscaleContext::kMaxQueueDepth;
            });

            ticket = ctx->nextTicket++;
            AsyncUpscaleOp op;
            op.ticket = ticket;
            op.srcTexture = src;
            op.dstTexture = nullptr;
            op.completed = false;
            op.failed = false;
            op.errorCode = CATRA_OK;
            ctx->asyncQueue.push_back(std::move(op));
        }
        ctx->asyncCv.notify_one();

        return ticket;
    });
}

int catra_upscale_poll_result(int ctx_handle, int ticket, void** dst_texture)
{
    return GuardCabi([&]() -> int {
        if (dst_texture != nullptr)
        {
            *dst_texture = nullptr;
        }

        UpscaleContext* ctx = LookupUpscale(ctx_handle);
        if (ctx == nullptr)
        {
            // Context destroyed - treat as "not found" rather than error
            // The pipeline may still be polling for tickets that were in flight
            return CATRA_ERR_UNKNOWN;
        }
        if (dst_texture == nullptr)
        {
            return CATRA_ERR_INVALID_ARG;
        }

        std::lock_guard<std::mutex> lock(ctx->asyncMutex);

        // Find the result
        for (auto it = ctx->asyncResults.begin(); it != ctx->asyncResults.end(); ++it)
        {
            if (it->ticket == ticket)
            {
                if (!it->completed)
                {
                    return CATRA_ERR_UNKNOWN; // Still in flight
                }

                if (it->failed)
                {
                    int errCode = it->errorCode;
                    ctx->asyncResults.erase(it);
                    return errCode;
                }

                *dst_texture = it->dstTexture; // Transfer ownership
                ctx->asyncResults.erase(it);
                ctx->asyncCv.notify_one(); // Wake up submitter if queue was full
                return CATRA_OK;
            }
        }

        // Ticket not found in results - check if still in queue
        for (const auto& op : ctx->asyncQueue)
        {
            if (op.ticket == ticket)
            {
                return CATRA_ERR_UNKNOWN; // Still in queue, not processed yet
            }
        }

        // Ticket not found anywhere - treat as "not found" (context may have been
        // destroyed, or ticket was already collected). Return CATRA_ERR_UNKNOWN
        // so the pipeline can handle it gracefully.
        return CATRA_ERR_UNKNOWN;
    });
}

int catra_upscale_pending_count(int ctx_handle)
{
    return GuardCabi([&]() -> int {
        UpscaleContext* ctx = LookupUpscale(ctx_handle);
        if (ctx == nullptr)
        {
            return 0;
        }

        std::lock_guard<std::mutex> lock(ctx->asyncMutex);
        return static_cast<int>(ctx->asyncQueue.size() + ctx->asyncResults.size());
    });
}

// ===========================================================================
// NV12 → BGRA GPU compute shader (ST-23)
// ===========================================================================
//
// Wraps catra::nv12_bgra_* behind the C ABI barrier. The compute shader
// converts a single NV12 array slice (from the FFmpeg D3D11VA decoder texture
// array) into a standalone BGRA texture entirely on the GPU. When the GPU
// path fails the C# caller falls back to the CPU path (av_hwframe_transfer_data
// + sws_scale + upload). The shader is compiled once at init and cached.

int catra_nv12_bgra_init(void* d3d11_device)
{
    return GuardCabi([&]() -> int {
        if (d3d11_device == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_nv12_bgra_init: null device");
            return CATRA_ERR_INVALID_ARG;
        }
        return catra::nv12_bgra_init(static_cast<ID3D11Device*>(d3d11_device));
    });
}

int catra_nv12_bgra_convert(void* d3d11_device, void* d3d11_ctx,
                             void* nv12_array_tex, unsigned int array_slice,
                             unsigned int width, unsigned int height,
                             void** out_bgra_tex)
{
    return GuardCabi([&]() -> int {
        if (out_bgra_tex != nullptr)
        {
            *out_bgra_tex = nullptr;
        }
        if (d3d11_device == nullptr || d3d11_ctx == nullptr ||
            nv12_array_tex == nullptr || out_bgra_tex == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_nv12_bgra_convert: null argument");
            return CATRA_ERR_INVALID_ARG;
        }
        return catra::nv12_bgra_convert(
            static_cast<ID3D11Device*>(d3d11_device),
            static_cast<ID3D11DeviceContext*>(d3d11_ctx),
            static_cast<ID3D11Texture2D*>(nv12_array_tex),
            array_slice, width, height,
            reinterpret_cast<ID3D11Texture2D**>(out_bgra_tex));
    });
}

void catra_nv12_bgra_shutdown(void)
{
    GuardCabiVoid([&]() {
        catra::nv12_bgra_shutdown();
    });
}

// ===========================================================================
// NV12 → BGRA CPU staging path — AMD RDNA 4 workaround
// ===========================================================================
//
// Bypasses av_hwframe_transfer_data (which produces zeros on AMD RDNA 4 for
// D3D11VA NV12 decoder textures) by using the proven full-texture CopyResource
// + two Map calls mechanism in d3d_interop.cpp's ConvertNv12ToBgra11. The
// bridge must be initialized (catra_init) so this function can use the same
// D3D11 device + immediate context the decoder lives on.

namespace catra {
// Defined in d3d_interop.cpp. Wrapper around the anonymous-namespace
// ConvertNv12ToBgra11. targetSlice selects the array slice.
Microsoft::WRL::ComPtr<ID3D11Texture2D> Nv12ToBgraStaging(
    ID3D11Texture2D* src,
    const D3D11_TEXTURE2D_DESC& srcDesc,
    unsigned int targetSlice);
} // namespace catra

int catra_nv12_staging_convert(void* nv12_tex,
                               unsigned int array_slice,
                               unsigned int width,
                               unsigned int height,
                               void** out_bgra_tex)
{
    return GuardCabi([&]() -> int {
        if (out_bgra_tex != nullptr)
        {
            *out_bgra_tex = nullptr;
        }
        if (nv12_tex == nullptr || out_bgra_tex == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_nv12_staging_convert: null argument");
            return CATRA_ERR_INVALID_ARG;
        }
        if (!g_initialized.load() || !g_device.Get() || !g_deviceContext.Get())
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra_nv12_staging_convert: bridge not initialized");
            return CATRA_ERR_INIT;
        }

        ID3D11Texture2D* src = static_cast<ID3D11Texture2D*>(nv12_tex);
        (void)width;
        (void)height;

        D3D11_TEXTURE2D_DESC desc = {};
        src->GetDesc(&desc);

    #if defined(DEBUG) || defined(_DEBUG)
        catra::BackendLog(CATRA_LOG_INFO,
               "catra_nv12_staging_convert: src fmt=%d w=%u h=%u arr=%u "
               "bind=0x%02X slice=%u vis=%ux%u",
               static_cast<int>(desc.Format), desc.Width, desc.Height,
               desc.ArraySize, static_cast<unsigned>(desc.BindFlags),
               array_slice, width, height);
    #endif

        Microsoft::WRL::ComPtr<ID3D11Texture2D> bgra =
            catra::Nv12ToBgraStaging(src, desc, array_slice);
        if (!bgra)
        {
            catra::BackendLog(CATRA_LOG_ERROR,
                       "catra_nv12_staging_convert: ConvertNv12ToBgra11 failed");
            return CATRA_ERR_DEVICE;
        }

        // Detach: caller owns the reference (catra_release_texture when done).
        *out_bgra_tex = bgra.Detach();
        return CATRA_OK;
    });
}

// ===========================================================================
// Encode — AMF H.265 (HEVC) backend (ST-16)
// ===========================================================================
//
// The AMF backend (encode_amf.cpp) lights up when the bridge is built against
// the GPUOpen AMF headers (CATRA_HAS_AMF via -DCATRA_AMF_ROOT); without them it
// compiles to a stub and these entry points surface CATRA_ERR_NOT_IMPL
// gracefully. The encoder runs on the bridge's shared D3D12 device (ST-15
// interop), so a create with no D3D12 device fails at the backend with
// CATRA_ERR_DEVICE. Encoded packets are context-owned (valid until the next
// encode/flush on the same context) — the frozen C ABI has no free call.

int catra_encode_create(int width, int height,
                        int bitrate_kbps, double fps, int* out_ctx)
{
    return GuardCabi([&]() -> int {
        if (out_ctx != nullptr)
        {
            *out_ctx = -1;
        }
        if (!g_initialized.load())
        {
            log_msg(CATRA_LOG_ERROR, "catra_encode_create: bridge not initialized");
            return CATRA_ERR_INIT;
        }
        if (width <= 0 || height <= 0 || bitrate_kbps <= 0 || fps <= 0.0 ||
            out_ctx == nullptr)
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra_encode_create: invalid args (%dx%d, %d kbps, %.3f fps)",
                    width, height, bitrate_kbps, fps);
            return CATRA_ERR_INVALID_ARG;
        }

        auto ctx = std::make_unique<EncodeContext>();
        ctx->width = width;
        ctx->height = height;
        ctx->bitrateKbps = bitrate_kbps;
        ctx->fps = fps;

        // The backend validates the D3D12 device: with CATRA_HAS_AMF it returns
        // CATRA_ERR_DEVICE when g_d3d12Device is null (interop unavailable);
        // without CATRA_HAS_AMF it returns CATRA_ERR_NOT_IMPL regardless. The
        // device is passed through (not pre-checked) so a no-headers build always
        // degrades to NOT_IMPL, per the ST-16 graceful-degradation requirement.
        const int rc = catra::AmfEncoder::Create(g_d3d12Device.Get(), width, height,
                                                 bitrate_kbps, fps, ctx->encoder);
        if (rc != CATRA_OK)
        {
            // Backend construction failed; nothing was registered, so the
            // unique_ptr drops any partial state on scope exit (RAII).
            log_msg(CATRA_LOG_ERROR,
                    "catra_encode_create: backend create failed rc=%d (amf_compiled=%d)",
                    rc, catra::AmfIsCompiled() ? 1 : 0);
            return rc;
        }

        log_msg(CATRA_LOG_INFO,
                "catra_encode_create: HEVC %dx%d @ %.3f fps, %d kbps",
                width, height, fps, bitrate_kbps);

        const int handle = RegisterEncode(std::move(ctx));
        *out_ctx = handle;
        return CATRA_OK;
    });
}

int catra_encode_frame(int ctx, void* texture,
                       uint8_t** out_buf, int* out_size)
{
    // Every failure path leaves *out_buf null / *out_size 0; the backend releases
    // no caller resource (it only reads the texture) and the C ABI guard converts
    // a stray exception (e.g. std::bad_alloc from the staging buffer) into
    // CATRA_ERR_UNKNOWN so nothing unwinds into the P/Invoke frame.
    return GuardCabi([&]() -> int {
        if (out_buf != nullptr)
        {
            *out_buf = nullptr;
        }
        if (out_size != nullptr)
        {
            *out_size = 0;
        }

        EncodeContext* c = LookupEncode(ctx);
        if (c == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_encode_frame: unknown context %d", ctx);
            return CATRA_ERR_CONTEXT;
        }
        if (texture == nullptr || out_buf == nullptr || out_size == nullptr)
        {
            return CATRA_ERR_INVALID_ARG;
        }

        // The pipeline hands us EITHER a D3D11 texture (decoder frame or RIFE
        // intermediate, passthrough upscale) or an ALREADY-D3D12 texture (FSR 1
        // / FSR 4 upscale output). The AMF encoder consumes D3D12, so D3D11
        // input goes through the pooled interop. The API must be PROBED, never
        // assumed: casting a D3D12 resource to ID3D11Texture2D is vtable UB
        // (ID3D11Texture2D::GetDesc slot 11 lands on ID3D12Resource::Unmap ->
        // garbage desc -> spurious CATRA_ERR_DEVICE or a device fault).
        //
        // AMF WORKAROUND (docs/FSR_AMF_ISSUE_CONTEXT.md, SPRINT_04 subtask 03):
        // D3D12 input is NOT handed to AMF zero-copy anymore —
        // CreateSurfaceFromDX12Native rejects every user-created D3D12 texture
        // (FSR 1/FSR 4 upscale output) but accepts the interop pool-slot
        // resources. Every D3D12 input is therefore copied through the D3D11
        // pool path (interop_copy_d3d12_for_encode) and AMF receives the pool
        // slot. GENERIC: no branch by origin backend — only the existing
        // D3D12-vs-D3D11 probe below.
        ID3D12Resource* d3d12res = nullptr;
        HANDLE sharedHandle = nullptr;
        int irc = CATRA_OK;

        ID3D12Resource* probed12 = nullptr;
        HRESULT qhr = static_cast<IUnknown*>(texture)->QueryInterface(IID_PPV_ARGS(&probed12));
        if (SUCCEEDED(qhr))
        {
            // Already D3D12 (upscale output): route through the encode
            // workaround. The helper returns an AddRef'd pool-slot resource
            // with the SAME output contract as interop_share_d3d11_to_d3d12
            // (*out_shared_handle stays null — the pool owns its handles), so
            // the ShareCleanup guard below covers it unchanged. The QI
            // reference is ours and is dropped right after the call.
            irc = catra::interop_copy_d3d12_for_encode(probed12, &d3d12res, &sharedHandle);
            if (probed12 != nullptr)
            {
                probed12->Release(); // ref from the QI is ours
            }
#if defined(DEBUG) || defined(_DEBUG)
            log_msg(CATRA_LOG_INFO,
                    "catra_encode_frame: texture is D3D12 -> D3D11 copy workaround (AMF), texture=%p",
                    texture);
#endif
        }
        else
        {
#if defined(DEBUG) || defined(_DEBUG)
            log_msg(CATRA_LOG_INFO, "catra_encode_frame: texture is D3D11 (QI hr=0x%08lX), doing interop",
                    static_cast<unsigned long>(qhr));
#endif
            // D3D11 input: share (or pooled-copy) onto the bridge's shared
            // adapter via the ST-15 interop.
            irc = catra::interop_share_d3d11_to_d3d12(
                static_cast<ID3D11Texture2D*>(texture), &d3d12res, &sharedHandle);
        }
        if (irc != CATRA_OK || d3d12res == nullptr)
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra_encode_frame: interop to D3D12 failed rc=%d", irc);
            // Defensive: the interop contract guarantees null outputs on
            // failure, but drop any partial handoff so nothing leaks even if
            // that contract is ever violated.
            if (sharedHandle != nullptr)
            {
                CloseHandle(sharedHandle);
            }
            if (d3d12res != nullptr)
            {
                d3d12res->Release();
            }
            return irc != CATRA_OK ? irc : CATRA_ERR_DEVICE;
        }

        // RAII cleanup for the interop handoff (root-cause fix for
        // DXGI_ERROR_DEVICE_REMOVED): interop_share_d3d11_to_d3d12 hands the
        // caller (a) a freshly minted NT handle that MUST be CloseHandle'd
        // (~1 leaked per frame exhausts the process handle table at ~972k
        // handles over a 2h film) and (b) an AddRef'd D3D12 reference — the
        // pool keeps its OWN slot reference, so this extra caller ref must be
        // dropped after Encode consumed the resource. The scope guard runs on
        // EVERY exit path (success, encode failure, and exception unwinding
        // caught by GuardCabi above), so neither resource can ever leak.
        struct ShareCleanup
        {
            ID3D12Resource* res = nullptr;
            HANDLE handle = nullptr;

            ~ShareCleanup()
            {
                // Closing the NT handle is safe before releasing the resource:
                // the opened D3D12 resource (or the pool slot) holds its own
                // reference to the shared allocation.
                if (handle != nullptr)
                {
                    CloseHandle(handle);
                }
                if (res != nullptr)
                {
                    res->Release();
                }
            }
        } cleanup{d3d12res, sharedHandle};

        int enc_rc = c->encoder->Encode(d3d12res, out_buf, out_size);

        // cleanup's destructor releases the caller's extra D3D12 ref and
        // closes the NT handle here (after Encode). The pool slot itself
        // stays intact for round-robin recycling.
        return enc_rc;
    });
}

int catra_encode_flush(int ctx, uint8_t** out_buf, int* out_size)
{
    return GuardCabi([&]() -> int {
        if (out_buf != nullptr)
        {
            *out_buf = nullptr;
        }
        if (out_size != nullptr)
        {
            *out_size = 0;
        }

        EncodeContext* c = LookupEncode(ctx);
        if (c == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_encode_flush: unknown context %d", ctx);
            return CATRA_ERR_CONTEXT;
        }
        if (out_buf == nullptr || out_size == nullptr)
        {
            return CATRA_ERR_INVALID_ARG;
        }

        return c->encoder->Flush(out_buf, out_size);
    });
}

void catra_encode_destroy(int ctx)
{
    fprintf(stderr, "catra_encode_destroy: ENTER ctx=%d\n", ctx);
    GuardCabiVoid([&]() {
        fprintf(stderr, "catra_encode_destroy: inside guard, calling DestroyEncode\n");
        DestroyEncode(ctx);
        fprintf(stderr, "catra_encode_destroy: DestroyEncode returned\n");
    });
    fprintf(stderr, "catra_encode_destroy: EXIT\n");
}

// ===========================================================================
// FG playback — FSR 3 Frame Generation via FFX FG swapchain (SPRINT_04
// subtask 04)
// ===========================================================================
//
// The internal renderer (catra_fg.cpp) owns the FFX FG swapchain + FG context
// and the letterbox D3D12 pipeline; it borrows the bridge's interop D3D12
// device + DIRECT queue (g_d3d12Device / g_d3d12Queue). Availability is
// runtime-loaded (ffx_runtime): loader + 8 list-A1 DLLs + a DX12 adapter.

int catra_is_fg_available(void)
{
    return GuardCabi([&]() -> int {
        // Needs a live bridge (device/queue come from catra_init's interop).
        if (!g_initialized.load())
        {
            return 0;
        }
        if (g_d3d12Device == nullptr || g_d3d12Queue == nullptr)
        {
            return 0; // interop soft-failed at init
        }
        return catra::ffx::FfxRuntime::IsAvailable() ? 1 : 0;
    });
}

int catra_fg_create(void* hwnd, int w, int h, double video_fps, int* out_ctx)
{
    return GuardCabi([&]() -> int {
        if (out_ctx != nullptr)
        {
            *out_ctx = -1;
        }
        if (!g_initialized.load())
        {
            log_msg(CATRA_LOG_ERROR, "catra_fg_create: bridge not initialized");
            return CATRA_ERR_INIT;
        }
        // HWND validation happens here (needs IsWindow) and again defensively
        // in FgRenderer::Create; w/h/fps validated in both places.
        if (hwnd == nullptr || !IsWindow(static_cast<HWND>(hwnd)) ||
            w <= 0 || h <= 0 || video_fps <= 0.0 || out_ctx == nullptr)
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra_fg_create: invalid args (hwnd=%p %dx%d fps=%.3f)",
                    hwnd, w, h, video_fps);
            return CATRA_ERR_INVALID_ARG;
        }
        if (g_d3d12Device == nullptr || g_d3d12Queue == nullptr)
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra_fg_create: no interop D3D12 device/queue");
            return CATRA_ERR_DEVICE;
        }

        catra::FgRenderer* renderer = nullptr;
        const int rc = catra::FgRenderer::Create(
            g_d3d12Device.Get(), g_d3d12Queue.Get(), hwnd, w, h, video_fps,
            &renderer);
        if (rc != CATRA_OK || renderer == nullptr)
        {
            log_msg(CATRA_LOG_ERROR,
                    "catra_fg_create: FgRenderer::Create failed rc=%d", rc);
            return rc != CATRA_OK ? rc : CATRA_ERR_DEVICE;
        }

        const int handle = RegisterFg(std::unique_ptr<catra::FgRenderer>(renderer));
        *out_ctx = handle;
        return CATRA_OK;
    });
}

int catra_fg_present(int ctx, void* frame_texture, int frame_w, int frame_h)
{
    return GuardCabi([&]() -> int {
        catra::FgRenderer* r = LookupFg(ctx);
        if (r == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_fg_present: unknown context %d", ctx);
            return CATRA_ERR_CONTEXT;
        }
        return r->Present(frame_texture, frame_w, frame_h);
    });
}

int catra_fg_resize(int ctx, int w, int h)
{
    return GuardCabi([&]() -> int {
        catra::FgRenderer* r = LookupFg(ctx);
        if (r == nullptr)
        {
            log_msg(CATRA_LOG_ERROR, "catra_fg_resize: unknown context %d", ctx);
            return CATRA_ERR_CONTEXT;
        }
        return r->Resize(w, h);
    });
}

void catra_fg_destroy(int ctx)
{
    GuardCabiVoid([&]() {
        DestroyFg(ctx); // no-op for an unknown handle
    });
}

// ===========================================================================
// Device health-check (ST-15 / SPRINT_05)
// ===========================================================================

int catra_device_health(int* out_reason)
{
    return GuardCabi([&]() -> int {
        HRESULT hr = S_OK;
        int quirk = 0;
        int rc = catra::interop_device_health(&hr, &quirk);
        if (out_reason != nullptr)
        {
            *out_reason = static_cast<int>(hr);
        }
        // quirk value consumed internally — when CATRA_OK + quirk=1 the
        // pipeline is functional via pool without keyed mutex; no action needed.
        (void)quirk;
        return rc;
    });
}

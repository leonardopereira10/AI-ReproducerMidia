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

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <mutex>

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
        g_deviceContext.Reset();
        g_device.Reset();

        // ST-15: flush + release the D3D12 command queue and device here.
        log_msg(CATRA_LOG_INFO, "catra_shutdown: bridge shut down");
    });
}

int catra_get_upscale_mode(void)
{
    // Skeleton: upscaling disabled. ST-14 selects FSR1/FSR4 at runtime.
    return CATRA_UPSCALE_OFF;
}

int catra_is_fsr4_available(void)
{
    // Skeleton: FSR 4 not probed yet. ST-14 checks the adapter + SDK.
    return 0;
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
// Upscale — stubs (ST-14)
// ===========================================================================

int catra_upscale_create(int src_w, int src_h,
                         int dst_w, int dst_h,
                         int method, int* out_ctx)
{
    (void)src_w; (void)src_h; (void)dst_w; (void)dst_h; (void)method;
    if (out_ctx != nullptr)
    {
        *out_ctx = -1;
    }
    return CATRA_ERR_NOT_IMPL;
}

int catra_upscale_process(int ctx, void* src_texture, void** dst_texture)
{
    (void)ctx; (void)src_texture;
    if (dst_texture != nullptr)
    {
        *dst_texture = nullptr;
    }
    return CATRA_ERR_NOT_IMPL;
}

void catra_upscale_destroy(int ctx)
{
    (void)ctx;
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

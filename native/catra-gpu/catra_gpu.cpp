// catra_gpu.cpp — CATRA GPU native bridge implementation (ST-12 skeleton).
//
// Lifecycle + capability queries are wired; the heavy backends (interp /
// upscale / encode) return CATRA_ERR_NOT_IMPL until ST-13..ST-16. The log
// callback is stored and used to surface diagnostics to the C# layer.

#define CATRA_GPU_BUILDING // export the CATRA_API symbols from this TU
#include "catra_gpu.h"

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <mutex>

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

} // namespace

// ===========================================================================
// Lifecycle
// ===========================================================================

int catra_init(void* d3d11_device)
{
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

    // ST-15: derive the DXGI adapter from d3d11_device and create the D3D12
    // device + command queue / allocator lists here. For the skeleton we only
    // record that the bridge is up so the C# layer can proceed.
    log_msg(CATRA_LOG_INFO, "catra_init: bridge initialized (skeleton)");
    return CATRA_OK;
}

void catra_shutdown(void)
{
    if (!g_initialized.exchange(false))
    {
        return; // idempotent
    }

    // ST-15: flush + release the D3D12 command queue, device and any live
    // interp/upscale/encode contexts.
    log_msg(CATRA_LOG_INFO, "catra_shutdown: bridge shut down");
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
    // Skeleton: interpolation backend not implemented.
    return CATRA_ERR_NOT_IMPL;
}

void catra_set_log_callback(catra_log_callback cb)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    g_logCallback = cb;
}

// ===========================================================================
// Frame interpolation — stubs (ST-13)
// ===========================================================================

int catra_interp_create(int src_w, int src_h,
                        double src_fps, double target_fps,
                        int method, int* out_ctx)
{
    (void)src_w; (void)src_h; (void)src_fps; (void)target_fps; (void)method;
    if (out_ctx != nullptr)
    {
        *out_ctx = -1;
    }
    return CATRA_ERR_NOT_IMPL;
}

int catra_interp_process(int ctx,
                         void* frame_a, void* frame_b,
                         void** out_frames, int* out_count)
{
    (void)ctx; (void)frame_a; (void)frame_b;
    if (out_frames != nullptr)
    {
        *out_frames = nullptr;
    }
    if (out_count != nullptr)
    {
        *out_count = 0;
    }
    return CATRA_ERR_NOT_IMPL;
}

void catra_interp_destroy(int ctx)
{
    (void)ctx; // nothing to release in the skeleton
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

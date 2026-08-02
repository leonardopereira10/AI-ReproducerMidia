// catra_gpu.h — CATRA GPU native bridge: flat C ABI for P/Invoke (ST-12).
//
// This header defines the *entire* native surface consumed by the C# layer
// (CATRA.Services/Processing/NativeBridge.cs). The ABI is intentionally flat
// (extern "C", POD args, int return codes) so it can be marshalled by
// DllImport without any C++ name mangling or struct layout concerns.
//
// CONVENTIONS
//   * Every fallible function returns `int`: 0 (CATRA_OK) on success, a
//     negative CATRA_ERR_* code on failure.
//   * Opaque per-job handles are handed out through `int* out_ctx` and echoed
//     back as the first `int ctx` argument. Handles are dense, non-negative
//     integers; 0 is a valid handle, negative is never a handle.
//   * GPU resources cross the boundary as raw `void*` (ID3D11Texture2D* /
//     ID3D12Resource*). Ownership stays with the caller unless documented.
//   * The log callback is invoked on whatever thread produced the message;
//     the callee must be thread-safe and must not call back into the bridge.
//
// STATUS (ST-12): skeleton only. Lifecycle (init/shutdown) and capability
// queries are wired; interp / upscale / encode return CATRA_ERR_NOT_IMPL.
// Real FSR 4 / RIFE / AMF backends land in ST-13..ST-16.

#ifndef CATRA_GPU_H
#define CATRA_GPU_H

#include <stddef.h>
#include <stdint.h>

// ---------------------------------------------------------------------------
// Export macro
// ---------------------------------------------------------------------------
#if defined(_WIN32)
    #if defined(CATRA_GPU_BUILDING)
        #define CATRA_API extern "C" __declspec(dllexport)
    #else
        #define CATRA_API extern "C" __declspec(dllimport)
    #endif
#else
    #define CATRA_API extern "C" __attribute__((visibility("default")))
#endif

// ---------------------------------------------------------------------------
// Result codes
// ---------------------------------------------------------------------------
#define CATRA_OK              0   // success
#define CATRA_ERR_INIT       -1   // bridge not initialized / init failed
#define CATRA_ERR_NOT_IMPL   -2   // function not implemented yet (skeleton)
#define CATRA_ERR_INVALID_ARG -3  // null / out-of-range argument
#define CATRA_ERR_DEVICE     -4   // D3D device / resource failure
#define CATRA_ERR_CONTEXT    -5   // unknown or stale context handle

// ---------------------------------------------------------------------------
// Capability enumerations (returned by the query functions)
// ---------------------------------------------------------------------------
// catra_get_upscale_mode():
#define CATRA_UPSCALE_OFF    0
#define CATRA_UPSCALE_FSR1   1
#define CATRA_UPSCALE_FSR4   2

// catra_get_interp_method():
#define CATRA_INTERP_NONE    0
#define CATRA_INTERP_RIFE    1
#define CATRA_INTERP_FSR3FG  2

// Log levels passed to the log callback.
#define CATRA_LOG_DEBUG      0
#define CATRA_LOG_INFO       1
#define CATRA_LOG_WARN       2
#define CATRA_LOG_ERROR      3

// Signature of the managed/unmanaged log sink. `msg` is a NUL-terminated
// UTF-8 string valid only for the duration of the call.
typedef void (*catra_log_callback)(const char* msg, int level);

#ifdef __cplusplus
extern "C" {
#endif

// ===========================================================================
// Lifecycle
// ===========================================================================

// Initializes the bridge on top of the caller's D3D11 device (shared so the
// GPU-resident FFmpeg textures can be handed across without a copy).
// `d3d11_device` is an ID3D11Device*. Returns CATRA_OK / CATRA_ERR_*.
CATRA_API int  catra_init(void* d3d11_device);

// Tears down the bridge and releases every owned resource. Idempotent.
CATRA_API void catra_shutdown(void);

// Current upscale mode (CATRA_UPSCALE_*). Skeleton: CATRA_UPSCALE_OFF (0).
CATRA_API int  catra_get_upscale_mode(void);

// Non-zero if FSR 4 is usable on the active adapter. Skeleton: 0.
CATRA_API int  catra_is_fsr4_available(void);

// Active interpolation method (CATRA_INTERP_*). Skeleton: CATRA_ERR_NOT_IMPL.
CATRA_API int  catra_get_interp_method(void);

// Installs (or clears, with NULL) the log sink. Safe to call before init.
CATRA_API void catra_set_log_callback(catra_log_callback cb);

// ===========================================================================
// Frame interpolation (RIFE / FSR 3 FG) — stubs
// ===========================================================================

// Creates an interpolation job. On success writes a handle to *out_ctx.
CATRA_API int  catra_interp_create(int src_w, int src_h,
                                   double src_fps, double target_fps,
                                   int method, int* out_ctx);

// Feeds a consecutive frame pair (ID3D11Texture2D* / ID3D12Resource*) and
// produces the intermediate frames. On success writes the output frame array
// to *out_frames and its length to *out_count, and returns the frame count
// (>= 0). Negative return is a CATRA_ERR_* code.
CATRA_API int  catra_interp_process(int ctx,
                                    void* frame_a, void* frame_b,
                                    void** out_frames, int* out_count);

// Destroys an interpolation job. No-op for an unknown handle.
CATRA_API void catra_interp_destroy(int ctx);

// ===========================================================================
// Upscale (FSR 4 / FSR 1) — stubs
// ===========================================================================

CATRA_API int  catra_upscale_create(int src_w, int src_h,
                                    int dst_w, int dst_h,
                                    int method, int* out_ctx);

// Upscales one texture. On success writes the destination texture to
// *dst_texture and returns CATRA_OK.
CATRA_API int  catra_upscale_process(int ctx,
                                     void* src_texture,
                                     void** dst_texture);

CATRA_API void catra_upscale_destroy(int ctx);

// ===========================================================================
// Encode (AMF H.265) — stubs
// ===========================================================================

CATRA_API int  catra_encode_create(int width, int height,
                                   int bitrate_kbps, double fps,
                                   int* out_ctx);

// Encodes one texture. On success writes the packet bytes to *out_buf and
// their size to *out_size and returns CATRA_OK. The buffer is owned by the
// context and valid until the next encode call on the same context.
CATRA_API int  catra_encode_frame(int ctx, void* texture,
                                  uint8_t** out_buf, int* out_size);

// Drains buffered packets. Same buffer contract as catra_encode_frame.
CATRA_API int  catra_encode_flush(int ctx,
                                  uint8_t** out_buf, int* out_size);

CATRA_API void catra_encode_destroy(int ctx);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // CATRA_GPU_H

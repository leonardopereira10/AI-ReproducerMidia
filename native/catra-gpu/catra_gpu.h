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
// STATUS (ST-14): lifecycle (init/shutdown), capability queries, frame
// interpolation and upscale are implemented. The RIFE backend is live when the
// bridge is built with ONNX Runtime (CATRA_HAS_ONNXRUNTIME; DirectML EP when
// USE_DML is detected, CPU fallback otherwise); without it, interp reports
// unavailable and returns CATRA_ERR_NOT_IMPL. Upscale (ST-14) runs FSR 4 when
// the bridge is built against the FidelityFX SDK (CATRA_HAS_FSR4) on an RDNA 4
// adapter and downgrades to the self-contained FSR 1 (EASU) otherwise; the
// DX12 dispatch path activates once the D3D11<->DX12 interop (ST-15) lands.
// Encode (AMF H.265, ST-16) remains a CATRA_ERR_NOT_IMPL stub.
//
// EXCEPTION SAFETY: every fallible entry point is guarded at the C ABI
// boundary; no C++ exception escapes this DLL. Stray exceptions (e.g.
// std::bad_alloc) surface as CATRA_ERR_UNKNOWN.

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
#define CATRA_ERR_UNKNOWN    -6   // unhandled C++ exception at the C ABI boundary

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

// Current upscale mode (CATRA_UPSCALE_*): the effective method of the most
// recently created upscale context (CATRA_UPSCALE_OFF before any create). The
// effective method reflects the FSR 4 -> FSR 1 downgrade, so a caller that
// requested FSR 4 on a non-RDNA 4 adapter sees CATRA_UPSCALE_FSR1 here.
CATRA_API int  catra_get_upscale_mode(void);

// Non-zero if FSR 4 is usable on the active adapter: built with the FidelityFX
// SDK (CATRA_HAS_FSR4), AMD adapter (VendorID 0x1002), RDNA 4, and a throw-away
// FSR 4 context initializes. 0 before catra_init or when any check fails.
CATRA_API int  catra_is_fsr4_available(void);

// Active interpolation method (CATRA_INTERP_*). Returns CATRA_INTERP_RIFE
// when the bridge was compiled with ONNX Runtime, CATRA_INTERP_NONE otherwise
// (no ONNX Runtime, or source fps already meets the target at create time).
CATRA_API int  catra_get_interp_method(void);

// Installs (or clears, with NULL) the log sink. Safe to call before init.
CATRA_API void catra_set_log_callback(catra_log_callback cb);

// ===========================================================================
// Frame interpolation — RIFE v4 backend (ST-13); FSR 3 FG is a later ST
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
// Upscale (FSR 4 / FSR 1) — ST-14
// ===========================================================================

// Creates an upscale job. `method`: 0 = off (passthrough copy), 1 = FSR 1
// (EASU, always available), 2 = FSR 4 (FidelityFX SDK, RDNA 4). When method=2
// but FSR 4 is unavailable (no SDK / not RDNA 4) the bridge logs a warning and
// downgrades to FSR 1; catra_get_upscale_mode then reports CATRA_UPSCALE_FSR1.
// On success writes a handle to *out_ctx.
CATRA_API int  catra_upscale_create(int src_w, int src_h,
                                    int dst_w, int dst_h,
                                    int method, int* out_ctx);

// Upscales one texture. `src_texture` is an ID3D11Texture2D* (src_w x src_h).
// On success writes the destination texture to *dst_texture and returns
// CATRA_OK. For passthrough the destination is an ID3D11Texture2D*; for
// FSR 1 / FSR 4 it is an ID3D12Resource* (shared back to D3D11 by the caller).
// The caller owns the returned texture (Release). *dst_texture stays null on
// every failure path.
CATRA_API int  catra_upscale_process(int ctx,
                                     void* src_texture,
                                     void** dst_texture);

// Destroys an upscale job. No-op for an unknown handle.
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

#ifndef CATRA_FG_H
#define CATRA_FG_H

// catra_fg.h — FSR 3 Frame Generation playback module (SPRINT_04 subtask 04).
//
// This header is INCLUDED AT THE END of catra_gpu.h (after the CATRA_API
// export macro and the CATRA_ERR_* codes are defined). Do not include it
// standalone and do not include catra_gpu.h from here (circular).
//
// The flat C ABI below (A3) is the playback-side Frame Generation surface:
// an FFX Frame Generation Swapchain (DX12) created directly on the caller's
// HWND (the WPF HwndHost window) + an FG context running with the runtime's
// INTERNAL optical flow (video has no game depth/motion vectors, so no
// Prepare dispatch is ever issued). Frames cross the D3D11->D3D12 interop
// (ST-15) and are letterboxed into the FG backbuffer; the proxy swapchain
// interpolates and paces the generated frames at the display rate.
//
// Entry points live in catra_gpu.cpp (GuardCabi barrier + registry, same
// pattern as upscale/interp/encode); the internal catra::FgRenderer lives in
// catra_fg.cpp.

// ===========================================================================
// Frame Generation playback — flat C ABI (A3)
// ===========================================================================

// Non-zero when the FG playback path is usable RIGHT NOW: catra_init has run
// with a live D3D11<->DX12 interop (g_d3d12Device present) AND the FFX
// runtime is fully available (loader + the 8 list-A1 runtime DLLs next to
// catra-gpu.dll + a DX12 adapter, FfxRuntime::IsAvailable). 0 otherwise
// (before catra_init, interop soft-failure, or any runtime probe failure).
CATRA_API int  catra_is_fg_available(void);

// Creates an FG playback context presenting on `hwnd`.
//
//   hwnd       valid top-level window (IsWindow); the FG swapchain takes
//              over presentation on it — the D3D11 renderer must NOT present
//              on the same window while the context is alive.
//   w, h       presentation viewport size (backbuffer / FG display size).
//   video_fps  informational (logs/diagnostics only — pacing of interpolated
//              frames belongs to the FG swapchain, driven by the display).
//   out_ctx    receives the dense handle on success (stays -1 on failure).
//
// Creates: DXGI factory + FFX FG swapchain (ForHwnd, DIRECT gameQueue) +
// FFX FG context (backend = the interop D3D12 device) + Configure(enabled)
// + the letterbox render pipeline on the two backbuffers. Any partial state
// is torn down before an error is returned (nothing half-alive leaks — this
// is the hook the automatic fallback of Story 05 relies on).
//
// Errors: CATRA_ERR_INIT (bridge not initialized), CATRA_ERR_INVALID_ARG
// (invalid hwnd / w / h / video_fps / out_ctx), CATRA_ERR_NOT_IMPL (FFX
// runtime unavailable: loader/DLLs/adapter), CATRA_ERR_DEVICE (swapchain /
// FG context / Configure / pipeline creation failure).
//
// Threading: NOT thread-safe per context; exactly one producer (the playback
// thread). FFX callbacks run on runtime threads; they never take bridge locks.
CATRA_API int  catra_fg_create(void* hwnd, int w, int h, double video_fps,
                               int* out_ctx);

// Presents one decoded frame through the FG swapchain.
//
//   frame_texture  ID3D11Texture2D* (BGRA, frame_w x frame_h). Caller-owned;
//                  the module only reads it (zero-copy share) and NEVER
//                  releases it.
//   frame_w, h     frame dimensions; when they differ from the context size
//                  the frame is letterbox-scaled (any direction) into the
//                  backbuffer, otherwise a fast-path copy runs.
//
// Sequence: interop share D3D11->D3D12 (keyed-mutex consumer half + NT
// handle cleanup) -> letterbox/copy into the current FG backbuffer ->
// Present(1, 0). The FG runtime interpolates and paces the output frames at
// the display rate; submission cadence belongs to the caller's playback
// clock (Story 05). The call MAY BLOCK up to ~one display frame in
// Present(1,0) (swapchain throttle/backpressure) — do not assume
// non-blocking; no additional internal pacing waits exist.
//
// frameID advances by exactly +1 per presented frame (FG runtime contract).
//
// Errors: CATRA_ERR_CONTEXT (unknown handle), CATRA_ERR_INVALID_ARG (null
// texture / non-positive dims), CATRA_ERR_DEVICE (dead context — includes
// DXGI_ERROR_DEVICE_REMOVED/RESET/HUNG; the context is marked dead and every
// following present/resize returns CATRA_ERR_DEVICE; destroy stays safe).
CATRA_API int  catra_fg_present(int ctx, void* frame_texture,
                                int frame_w, int frame_h);

// Recreates the FG swapchain + FG context at the new size on the SAME HWND
// (teardown first: Configure(enabled=false) — which flushes pending presents
// internally, avoiding debug-layer #921 OBJECT_DELETED_WHILE_STILL_IN_USE —
// then FG/swapchain context destroy, then recreate). frameID continues
// without a gap. Same size is a no-op returning CATRA_OK. Debouncing resize
// storms is the CALLER's job (Story 05); the native side only tolerates
// sequential calls. Errors: CATRA_ERR_CONTEXT, CATRA_ERR_INVALID_ARG
// (w/h <= 0), CATRA_ERR_DEVICE (dead context, or recreate failure — the
// context is then dead: CATRA_ERR_DEVICE from then on, destroy still safe).
CATRA_API int  catra_fg_resize(int ctx, int w, int h);

// Tears the context down: Configure(enabled=false) -> destroy FG context ->
// destroy swapchain context -> release the proxy swapchain (refcount must hit
// zero, logged otherwise) -> release render pipeline. Idempotent for unknown
// handles; safe on a dead context.
CATRA_API void catra_fg_destroy(int ctx);

// ===========================================================================
// Internal C++ surface (not exported through the DLL export table)
// ===========================================================================
#ifdef __cplusplus

namespace catra {

// Present observer (test hook only): called from the FFX present callback
// (runtime threads) once per REAL present — rendered AND generated frames —
// with is_generated_frame telling which. Must be fast, thread-safe and never
// call back into the bridge. Installed/cleared via FgSetPresentObserver.
typedef void (*FgPresentObserverFn)(bool is_generated_frame, void* user);

// FSR 3 Frame Generation playback renderer (pimpl; the header carries no COM
// types — everything crosses as void*/primitives). One instance owns: the FFX
// FG swapchain context (ForHwnd), the FFX FG context, the letterbox D3D12
// render pipeline and the per-frame share+present path. NOT thread-safe; one
// producer. Destroying the instance is the complete, idempotent teardown.
class FgRenderer
{
public:
    // device12/queue12: the bridge's interop D3D12 device + DIRECT queue
    // (borrowed — they outlive the renderer by shutdown ordering). hwnd must
    // be a valid window; w/h/video_fps as in catra_fg_create. Returns
    // CATRA_OK with *out owning the renderer, or CATRA_ERR_NOT_IMPL (FFX
    // runtime unavailable) / CATRA_ERR_DEVICE / CATRA_ERR_INVALID_ARG with
    // *out untouched. All partial state is cleaned on failure.
    static int Create(void* device12, void* queue12, void* hwnd,
                      int w, int h, double video_fps, FgRenderer** out);

    // Frame path (see catra_fg_present). CATRA_ERR_DEVICE once dead.
    int  Present(void* d3d11_texture, int frame_w, int frame_h);

    // Teardown + recreate at the new size (see catra_fg_resize).
    int  Resize(int w, int h);

    // Installs/clears the present observer (cb == nullptr clears).
    void SetPresentObserver(FgPresentObserverFn cb, void* user);

    ~FgRenderer(); // full teardown, idempotent

    // Pimpl state (defined in catra_fg.cpp). Public type name so the file-scope
    // helper functions of catra_fg.cpp (FFX trampolines, build/teardown passes)
    // can operate on it; the instance itself stays non-copyable.
    struct Impl;

private:
    FgRenderer() = default;
    FgRenderer(const FgRenderer&) = delete;
    FgRenderer& operator=(const FgRenderer&) = delete;

    Impl* m_impl = nullptr; // defined in catra_fg.cpp
};

// Test hook used ONLY by tools/fg_smoke_test.cpp (defined in catra_gpu.cpp,
// where the FG registry lives): installs `cb` on context `ctx`; no-op for an
// unknown handle. Not part of the C ABI, never exported.
void FgSetPresentObserver(int ctx, FgPresentObserverFn cb, void* user);

} // namespace catra

#endif // __cplusplus

#endif // CATRA_FG_H

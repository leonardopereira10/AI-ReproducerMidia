// interp_rife.h — RIFE v4 frame interpolation backend (ST-13).
//
// Implements the heavy lifting behind catra_interp_create/process/destroy:
// an ONNX Runtime InferenceSession running the RIFE v4 arbitrary-timestep
// model, accelerated by the DirectML execution provider (AMD RDNA 4) with an
// automatic CPU fallback.
//
// DATA FLOW (ST-13 scope)
//   input : two consecutive ID3D11Texture2D (B8G8R8A8 / R8G8B8A8, src_w x src_h)
//   model : img0 [1,3,H,W] + img1 [1,3,H,W] + timestep  ->  out [1,3,H,W]
//           (RGB float32, normalized to [0,1])
//   output: N intermediate ID3D11Texture2D (B8G8R8A8), one per timestep
//
//   N == frames_per_pair == ceil(target_fps / src_fps) - 1. Timesteps are
//   i / (N + 1) for i in [1..N], i.e. the interval [0,1] is split into N+1
//   equal segments. When src_fps >= target_fps (RN-07) N == 0 and the context
//   is a passthrough: process() yields zero frames and the caller keeps the
//   originals.
//
// NOTE (ST-15/17): tensors are marshalled through CPU memory at the ONNX
// Runtime boundary (the DirectML EP still performs the model math on the GPU;
// ORT copies across the EP boundary). Zero-copy D3D12 tensors and NV12 decode
// conversion land with the DX12 interop work; ST-13 deliberately stays on the
// well-understood D3D11 staging path so it is correct by inspection.
//
// THREADING: all entry points are internally synchronized; a single context is
// NOT safe to process from multiple threads concurrently (the ONNX session and
// the reused staging buffers are per-context).

#ifndef CATRA_INTERP_RIFE_H
#define CATRA_INTERP_RIFE_H

// Forward declarations only — keep this header free of <d3d11.h> so the C ABI
// header (catra_gpu.h) can stay lightweight.
struct ID3D11Device;
struct ID3D11DeviceContext;

namespace catra {

// True when the bridge was compiled with ONNX Runtime (CATRA_HAS_ONNXRUNTIME).
// When false, InterpRifeCreate returns CATRA_ERR_NOT_IMPL and the C ABI reports
// CATRA_INTERP_NONE as the active method.
bool InterpRifeIsCompiled();

// Creates an interpolation context. See catra_gpu.h for the argument contract.
// Borrows (does not own) the bridge D3D11 device + immediate context captured
// by catra_init; both must outlive the context.
int InterpRifeCreate(ID3D11Device* device,
                     ID3D11DeviceContext* deviceContext,
                     int srcW, int srcH,
                     double srcFps, double targetFps,
                     int method,
                     int* outCtx);

// Runs inference for one frame pair. On success allocates *outFrames as a
// `new void*[count]` array of AddRef'd ID3D11Texture2D* and writes the count.
// The caller owns the array (delete[]) and each texture (Release).
int InterpRifeProcess(int ctx,
                      void* frameA, void* frameB,
                      void** outFrames, int* outCount);

// Destroys one context. No-op for an unknown handle.
void InterpRifeDestroy(int ctx);

// Destroys every live context (called from catra_shutdown before the bridge
// releases its D3D11 device).
void InterpRifeDestroyAll();

} // namespace catra

#endif // CATRA_INTERP_RIFE_H

// nv12_to_bgra_shader.h — GPU compute shader NV12 → BGRA conversion (ST-23).
//
// Converts NV12 (Y plane R8 + interleaved UV plane R8G8) to BGRA entirely on
// the GPU via a D3D11 compute shader, eliminating the av_hwframe_transfer_data
// + sws_scale + BGRA upload round-trip through system memory. The input is a
// single array slice of the FFmpeg D3D11VA decoder texture array (ArraySize>1,
// NV12 format); the output is a standalone BGRA texture (ArraySize==1).
//
// The HLSL is embedded as a string in nv12_to_bgra_shader.cpp and compiled to
// DXBC at runtime via D3DCompile (d3dcompiler_47.dll), matching the pattern
// established by upscale_fsr1.cpp. No precompiled shader blob on disk.
//
// CONVERSION: BT.601 full-range (Y [0,1], UV [-0.5,0.5]):
//   R = Y + 1.402 * V
//   G = Y - 0.344136 * U - 0.714136 * V
//   B = Y + 1.772 * U
//
// THREADING: nv12_bgra_init / nv12_bgra_shutdown are NOT thread-safe (call once
// at decoder init/teardown). nv12_bgra_convert is safe to call from one thread
// at a time (the D3D11 immediate context is single-threaded by design).

#ifndef CATRA_NV12_TO_BGRA_SHADER_H
#define CATRA_NV12_TO_BGRA_SHADER_H

struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11Texture2D;

namespace catra {

// Compiles the NV12→BGRA compute shader via D3DCompile and caches the
// ID3D11ComputeShader on the module. Must be called once before the first
// convert call. Returns CATRA_OK on success, CATRA_ERR_DEVICE if shader
// compilation or pipeline creation fails. Idempotent (second call is a no-op
// returning CATRA_OK).
int nv12_bgra_init(ID3D11Device* device);

// Converts one NV12 array slice to a standalone BGRA texture.
//   device         — the bridge's D3D11 device (used to create the output
//                    texture and SRVs; must match the device that owns
//                    nv12ArrayTex).
//   ctx            — the D3D11 immediate context (used for Dispatch).
//   nv12ArrayTex   — the decoder's NV12 texture array (ArraySize > 1).
//   arraySlice     — the slice index (from AVFrame->data[1]).
//   width, height  — the visible frame dimensions (NOT the aligned surface).
//   outBgra        — on success, receives an AddRef'd ID3D11Texture2D*
//                    (ArraySize==1, BGRA, USAGE_DEFAULT, BIND_UAV|SRV).
//                    Caller owns the reference (Release when done).
//                    Stays null on every failure path.
// Returns CATRA_OK on success. Returns CATRA_ERR_INIT if init was not called,
// CATRA_ERR_INVALID_ARG for null/zero args, CATRA_ERR_DEVICE on GPU failure.
int nv12_bgra_convert(ID3D11Device* device,
                      ID3D11DeviceContext* ctx,
                      ID3D11Texture2D* nv12ArrayTex,
                      unsigned int arraySlice,
                      unsigned int width,
                      unsigned int height,
                      ID3D11Texture2D** outBgra);

// Releases the cached compute shader and any module-level state. Idempotent.
// Must be called from the same thread as init (or after the D3D11 device is
// released — COM refs are null-safe).
void nv12_bgra_shutdown();

// Returns non-zero if the shader was compiled and the pipeline is ready.
int nv12_bgra_is_ready();

} // namespace catra

#endif // CATRA_NV12_TO_BGRA_SHADER_H

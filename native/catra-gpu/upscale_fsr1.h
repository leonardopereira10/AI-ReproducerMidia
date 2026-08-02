// upscale_fsr1.h — FSR 1 (EASU) spatial upscale backend (ST-14).
//
// This is the ALWAYS-AVAILABLE fallback upscaler. Unlike FSR 4 (ML, RDNA 4
// only, license-gated SDK — see upscale_fsr4.h), FSR 1 is a pure spatial
// edge-adaptive upscaler implemented as a self-contained DX12 compute shader:
// no external SDK, no model weights, works on any DX12-capable adapter. When
// the FSR 4 SDK is absent or the adapter is not RDNA 4, the bridge downgrades
// method=2 (FSR 4) to method=1 (FSR 1) so the MVP still upscales (RN-07 /
// risk mitigation: availability over peak quality, offline pipeline).
//
// ALGORITHM
//   EASU (Edge Adaptive Spatial Upsampling), the first pass of AMD FSR 1. For
//   every output pixel the shader maps back into input space, gathers a 12-tap
//   neighbourhood, estimates the local edge direction from the 4 cardinal
//   2x2 quads, derives directional min/max clamps and clamps a separable
//   4-tap Lanczos-weighted result. This preserves edges without the ringing a
//   naive bicubic/Lanczos resize produces. The HLSL is embedded in
//   upscale_fsr1.cpp and compiled to DXBC at runtime via D3DCompile (the DX12
//   runtime translates DXBC->DXIL), so the backend needs neither dxc nor a
//   precompiled shader blob on disk.
//
// DATA FLOW (per catra_upscale_process)
//   input : ID3D11Texture2D (BGRA/RGBA, srcW x srcH) from the decoder/interp
//   interop: D3D11 -> DX12 shared resource (catra::ShareTexture, ST-15). Until
//            ST-15 lands ShareTexture is a stub returning E_NOTIMPL, so the DX12
//            dispatch path reports CATRA_ERR_NOT_IMPL at runtime; the backend is
//            nonetheless complete and correct by inspection and activates as soon
//            as the interop is wired.
//   dispatch: EASU compute (numthreads 16x16), one thread per output pixel
//   output: ID3D12Resource (dstW x dstH), shared back to D3D11 by the caller
//
// This header ALSO hosts the pure, GPU-free decision helpers shared by every
// upscale backend (quality-mode selection + FSR4->FSR1 method resolution).
// They live here because FSR 1 is the one backend that is always compiled, so
// the symbols are always linkable (the native test pins them directly).
//
// THREADING: a single Fsr1Upscaler is NOT safe to process from multiple threads
// concurrently (it reuses one command allocator / descriptor heap).

#ifndef CATRA_UPSCALE_FSR1_H
#define CATRA_UPSCALE_FSR1_H

#include <memory>

// Forward declarations only — keep this header free of <d3d11.h>/<d3d12.h> so
// it can be included by the lightweight C ABI translation units and the native
// test without dragging in the DirectX headers.
struct ID3D11Device;
struct ID3D11Texture2D;
struct ID3D12Device;
struct ID3D12Resource;

namespace catra {

// ---------------------------------------------------------------------------
// Pure decision helpers (no GPU, fully unit-testable)
// ---------------------------------------------------------------------------

// FidelityFX upscale quality presets. The enum mirrors the FidelityFX SDK
// ordering so upscale_fsr4.cpp can map it 1:1 onto ffx::QualityMode. FSR 1 has
// no quality modes of its own (EASU is a single fixed algorithm); the selected
// mode only influences the FSR 4 dispatch and is recorded for diagnostics.
enum class UpscaleQualityMode
{
    NativeAA = 0,        // 1.0x  (no upscale; not selected here)
    Quality = 1,         // ~1.5x (sharp, offline default)
    Balanced = 2,        // ~1.7x
    Performance = 3,     // ~2.0x
    UltraPerformance = 4 // ~3.0x (720p->4K)
};

// Selects the FidelityFX quality mode for a given source->display upscale.
//
// ROOT CAUSE / DESIGN: the ST-14 spec pins three anchors:
//   720p  -> 4K   (3.0x linear) => UltraPerformance
//   1080p -> 4K   (2.0x linear) => Quality
//   720p  -> 1080p (1.5x linear) => Quality
// A gaming FSR profile would map 2.0x to Performance, but CATRA is an OFFLINE
// video pipeline with zero latency constraint (RN-07), so it biases hard toward
// quality: every upscale below the extreme 3x case uses the Quality preset, and
// only the 3x+ case (where Quality cannot reconstruct enough detail) drops to
// UltraPerformance. This satisfies all three spec anchors exactly:
//   scale(720->2160)  = 3.0   -> UltraPerformance
//   scale(1080->2160) = 2.0   -> Quality
//   scale(720->1080)  = 1.5   -> Quality
// The ratio is measured on height (the conventional "p" resolution axis).
UpscaleQualityMode SelectQualityMode(int srcW, int srcH, int dstW, int dstH);

// Resolves the effective upscale method given what the caller requested and
// whether FSR 4 is actually usable.
//   requested 0 (off)        -> 0 (passthrough), always honoured
//   requested 1 (fsr1)       -> 1, always honoured
//   requested 2 (fsr4) + avail -> 2
//   requested 2 (fsr4) + !avail -> 1 (graceful downgrade, logged by caller)
//   anything else            -> 0 (defensive: unknown method => passthrough)
// `fsr4Available` is the boolean result of catra::Fsr4IsAvailable(). Pure.
int ResolveUpscaleMethod(int requested, bool fsr4Available);

// ---------------------------------------------------------------------------
// FSR 1 EASU upscaler (DX12 compute, self-contained HLSL)
// ---------------------------------------------------------------------------

class Fsr1Upscaler
{
public:
    Fsr1Upscaler() = default;
    ~Fsr1Upscaler();

    Fsr1Upscaler(const Fsr1Upscaler&) = delete;
    Fsr1Upscaler& operator=(const Fsr1Upscaler&) = delete;

    // Builds the DX12 compute pipeline (root signature, EASU PSO, descriptor
    // heap) on a D3D12 device created from the bridge's D3D11 adapter. Borrows
    // nothing that it does not AddRef. Returns a CATRA_* code:
    //   CATRA_OK            pipeline ready
    //   CATRA_ERR_INVALID_ARG  null device / non-positive dimensions
    //   CATRA_ERR_NOT_IMPL  the D3D11->DX12 interop is not wired yet (ST-15 stub)
    //   CATRA_ERR_DEVICE    shader compile / PSO / heap creation failed
    static int Create(ID3D11Device* d3d11Device,
                      int srcW, int srcH, int dstW, int dstH,
                      std::unique_ptr<Fsr1Upscaler>& out);

    // Upscales one frame. `src` is an ID3D11Texture2D* (srcW x srcH, BGRA/RGBA);
    // on success writes an AddRef'd ID3D12Resource* (dstW x dstH) to *outDst.
    // The caller owns the returned resource (Release). Returns a CATRA_* code.
    int Process(ID3D11Texture2D* src, ID3D12Resource** outDst);

    int SrcWidth() const { return m_srcW; }
    int SrcHeight() const { return m_srcH; }
    int DstWidth() const { return m_dstW; }
    int DstHeight() const { return m_dstH; }

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;
    int m_srcW = 0;
    int m_srcH = 0;
    int m_dstW = 0;
    int m_dstH = 0;
};

} // namespace catra

#endif // CATRA_UPSCALE_FSR1_H

// upscale_fsr4.h — FSR 4/3.1 (FidelityFX API 2.x) temporal upscale backend
// (ST-14, SPRINT_04 subtask 02 — rewritten from the compile-gated stub).
//
// The backend is ALWAYS compiled and RUNTIME-LOADED through the FFX API 2.x
// flat C surface: catra::ffx::FfxRuntime (ffx_runtime.h, SPRINT_04 subtask 01)
// LoadLibrary's amd_fidelityfx_loader_dx12.dll and resolves the 5 ffx entry
// points (ffxCreateContext / ffxDestroyContext / ffxConfigure / ffxQuery /
// ffxDispatch) via GetProcAddress. There is NO build-time dependency on any
// FidelityFX import lib — only the vendored MIT headers in
// lib/FidelityFX-SDK-2.3.0 (api/include + upscalers/include).
//
// AVAILABILITY (runtime, not build time):
//   Fsr4IsAvailable() == true only when the loader DLL + all 8 FFX runtime
//   DLLs (list A1) are present next to catra-gpu.dll AND a throw-away upscale
//   context initializes on the bridge's D3D12 adapter. The FFX runtime itself
//   decides between FSR 4 ML (RDNA 4) and the FSR 3.1 fallback on the adapter
//   — the backend does NOT pre-filter by vendor/device ID.
//   When unavailable the bridge downgrades method=2 (FSR 4) to method=1
//   (FSR 1 EASU, upscale_fsr1.h — untouched) so upscaling always works.
//
// VIDEO MODE (zero-MV): the FFX upscale API is temporal and expects color,
// depth, motionVectors and jitterOffset from a game engine. CATRA is a video
// pipeline with no engine MVs, so the backend feeds persistent zeroed dummies:
//   * motionVectors: R32G32_FLOAT, srcW x srcH, cleared to 0
//   * depth:         R32_FLOAT,    srcW x srcH, cleared to 0
//   * jitterOffset:  (0, 0) every frame
//   * reset=true on the first frame and after RequestSceneCut() (scene cut)
// Quality limitation (ghosting across cuts until a reset is signalled) is
// documented; FSR 1 (EASU) remains the export default.
//
// OUTPUT CONTRACT (PO decision D-PO-4): Process writes dstW x dstH
// DXGI_FORMAT_B8G8R8A8_UNORM (BGRA — aligns with FSR 1 and AMF_SURFACE_BGRA;
// RGBA was explicitly rejected, see docs/FSR_AMF_ISSUE_CONTEXT.md).
//
// THREADING: a single Fsr4Upscaler is not safe for concurrent Process calls.

#ifndef CATRA_UPSCALE_FSR4_H
#define CATRA_UPSCALE_FSR4_H

#include "upscale_fsr1.h" // UpscaleQualityMode

#include <memory>

struct ID3D11Device;
struct ID3D11Texture2D;
struct ID3D12Resource;

namespace catra {

class Fsr4Upscaler;

// Probes whether the FFX upscale runtime can actually run on the bridge's
// adapter (runtime-load semantics — no build-time gate):
//   1. the FFX loader loads (amd_fidelityfx_loader_dx12.dll + the 5 ffx
//      exports resolve; catra::ffx::FfxRuntime::Load),
//   2. all 8 FFX runtime DLLs (list A1) exist next to the module
//      (FfxRuntime::ProbeDependencyDlls),
//   3. a throw-away 64x64 -> 128x128 upscale context initializes on the D3D12
//      device of `d3d11Device`'s adapter (the definitive check — the FFX
//      runtime either finds a usable provider on this adapter or it does not;
//      it picks FSR 4 ML on RDNA 4 and FSR 3.1 elsewhere).
// Returns false on any failure; never throws. A null device returns false.
bool Fsr4IsAvailable(ID3D11Device* d3d11Device);

class Fsr4Upscaler
{
public:
    Fsr4Upscaler() = default;
    ~Fsr4Upscaler();

    Fsr4Upscaler(const Fsr4Upscaler&) = delete;
    Fsr4Upscaler& operator=(const Fsr4Upscaler&) = delete;

    // Creates the ffx upscale context (flat C, runtime-loaded). `quality` has
    // no field in the FFX 2.x create descriptor — it is kept for logging /
    // diagnostics only (see upscale_fsr1.h SelectQualityMode). Returns a
    // CATRA_* code:
    //   CATRA_OK              context ready
    //   CATRA_ERR_INVALID_ARG null device / non-positive dimensions
    //   CATRA_ERR_DEVICE      FFX runtime not loadable / DLLs missing /
    //                         D3D12 device or ffxCreateContext failed
    static int Create(ID3D11Device* d3d11Device,
                      int srcW, int srcH, int dstW, int dstH,
                      UpscaleQualityMode quality,
                      std::unique_ptr<Fsr4Upscaler>& out);

    // Upscales one frame: shares the D3D11 source into DX12, dispatches the
    // FFX upscale effect (zero-MV video mode), and writes an AddRef'd
    // ID3D12Resource* (dstW x dstH, B8G8R8A8_UNORM) to *outDst (caller owns
    // the reference). Returns a CATRA_* code.
    int Process(ID3D11Texture2D* src, ID3D12Resource** outDst);

    // Marks a scene cut: the NEXT Process dispatches with reset=true, which
    // flushes the temporal accumulation (a hard cut would otherwise ghost the
    // previous scene into the new one). No GPU work happens here — the flag
    // is consumed by the next dispatch. The very first Process after Create
    // always runs with reset=true. Returns CATRA_OK.
    // THREADING: same rule as Process — not thread-safe per instance.
    int RequestSceneCut();

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

#endif // CATRA_UPSCALE_FSR4_H

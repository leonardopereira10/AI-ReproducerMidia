// upscale_fsr4.h — FSR 4 (FidelityFX SDK) ML upscale backend (ST-14).
//
// FSR 4 is AMD's machine-learning upscaler: it runs on the dedicated ML
// accelerators of RDNA 4 GPUs (RX 9070 series) through DX12 compute shaders
// shipped by the FidelityFX SDK. Unlike FSR 1 (spatial, self-contained — see
// upscale_fsr1.h), FSR 4 depends on an EXTERNAL, license-gated SDK that is NOT
// vendored into this repository:
//
//   * The SDK is downloaded by the integrator from GPUOpen
//     (https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) and pointed
//     at via -DCATRA_FSR_SDK_ROOT=<path> at CMake configure time.
//   * When that path is absent, CMake does NOT define CATRA_HAS_FSR4 and this
//     backend compiles to an inert stub: Fsr4IsCompiled() == false,
//     Fsr4IsAvailable() == false, Create() == CATRA_ERR_NOT_IMPL. The bridge
//     then downgrades method=2 (FSR 4) to method=1 (FSR 1) so upscaling still
//     works (RN-07 / risk mitigation). FSR 1 carries the Phase-2 MVP.
//
// API SURFACE (when CATRA_HAS_FSR4 is defined)
//   The backend drives ffx::ContextUpscale (ffx_api/ffx_upscale.hpp). Create
//   builds the context with maxRenderSize/displaySize = dst and a qualityMode
//   mapped 1:1 from catra::UpscaleQualityMode; Process feeds the shared DX12
//   input resource and reads back the upscaled output. The D3D11<->DX12 sharing
//   reuses catra::ShareTexture (ST-15; a stub returning E_NOTIMPL for now).
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

// True iff the bridge was compiled against the FidelityFX SDK (CATRA_HAS_FSR4).
// When false, every other FSR 4 entry point degrades gracefully.
bool Fsr4IsCompiled();

// Probes whether FSR 4 can actually run on the bridge's adapter:
//   1. Fsr4IsCompiled() must be true (SDK present at build time),
//   2. the DXGI adapter behind `d3d11Device` must be AMD (VendorID 0x1002) and
//      RDNA 4 (device-ID heuristic + feature level),
//   3. a throw-away FSR 4 context must initialize (the definitive check — the
//      ML accelerators either exist or they do not).
// Returns false on any failure; never throws. A null device returns false.
bool Fsr4IsAvailable(ID3D11Device* d3d11Device);

class Fsr4Upscaler
{
public:
    Fsr4Upscaler() = default;
    ~Fsr4Upscaler();

    Fsr4Upscaler(const Fsr4Upscaler&) = delete;
    Fsr4Upscaler& operator=(const Fsr4Upscaler&) = delete;

    // Creates the ffx::ContextUpscale. Returns a CATRA_* code:
    //   CATRA_OK              context ready
    //   CATRA_ERR_NOT_IMPL    built without the FidelityFX SDK (CATRA_HAS_FSR4 off)
    //   CATRA_ERR_INVALID_ARG null device / non-positive dimensions
    //   CATRA_ERR_DEVICE      adapter not RDNA 4 / context init failed /
    //                         ST-15 interop not wired yet
    static int Create(ID3D11Device* d3d11Device,
                      int srcW, int srcH, int dstW, int dstH,
                      UpscaleQualityMode quality,
                      std::unique_ptr<Fsr4Upscaler>& out);

    // Upscales one frame: shares the D3D11 source into DX12, dispatches FSR 4,
    // and writes an AddRef'd ID3D12Resource* (dstW x dstH) to *outDst (caller
    // owns the reference). Returns a CATRA_* code.
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

#endif // CATRA_UPSCALE_FSR4_H

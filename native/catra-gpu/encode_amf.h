// encode_amf.h — AMF H.265 (HEVC) hardware encode backend (ST-16).
//
// Implements the heavy lifting behind catra_encode_create/frame/flush/destroy:
// an AMD Media Framework (AMF) video-encoder component
// (AMFVideoEncoderUVD_H265_MAIN) running on the dedicated encode block of an
// RDNA 4 GPU. The encoder is a fixed-function block separate from the compute
// units, so it does not contend with FSR 4 upscale (spec RF-03 / D9).
//
// Like FSR 4 (upscale_fsr4.h), AMF depends on an EXTERNAL SDK that is NOT
// vendored into this repository:
//
//   * The AMF SDK is HEADERS-ONLY and lives on GPUOpen
//     (https://github.com/GPUOpen-LibrariesAndSDKs/AMF). The integrator clones
//     it (lib/amf/ is gitignored) and points CMake at it via
//     -DCATRA_AMF_ROOT=<repo root>. The AMF *runtime* (amfrt64.dll) ships with
//     the AMD Adrenalin driver and is loaded dynamically at Create time — there
//     is NO link-time dependency on any AMF import lib.
//   * When CATRA_AMF_ROOT is absent, CMake does NOT define CATRA_HAS_AMF and
//     this backend compiles to an inert stub: AmfIsCompiled() == false,
//     Create() == CATRA_ERR_NOT_IMPL. The C ABI then surfaces CATRA_ERR_NOT_IMPL
//     gracefully to the caller (no crash, no DllNotFoundException equivalent).
//
// DATA FLOW (when CATRA_HAS_AMF is defined)
//   input : ID3D12Resource* — a GPU-resident NV12 texture on the bridge's D3D12
//           device (the interop device created by catra_init / ST-15). The
//           pipeline (ST-17) is responsible for handing the encoder NV12 frames
//           (RGB->NV12 conversion lives upstream; see interp_rife.h notes).
//   wrap  : AMFContext::CreateSurfaceFromDX12Native wraps the texture ZERO-COPY
//           as an AMFSurface (no GPU copy — the resource already lives on the
//           AMF device). AMF's encoder component owns an internal input-surface
//           reference queue (depth 4-8 under USAGE_TRANSCONDING); that queue is
//           the "surface pool" — we feed it wrappers instead of copying into a
//           private pool because the inputs are already device-resident.
//   encode: SubmitInput(surface) -> QueryOutput(buffer). The encoder is async:
//           a submit may not immediately yield a packet (buffering), in which
//           case Encode returns 0 bytes with CATRA_OK and the caller keeps
//           feeding frames. Output is an AMFBuffer of Annex B NAL units
//           (VPS/SPS/PPS inserted at GOP boundaries via HEADER_INSERTION_MODE).
//   output: the packet bytes are copied into a CONTEXT-OWNED staging buffer and
//           a pointer into it is returned, valid until the next Encode/Flush on
//           the SAME context. This matches the frozen C ABI (catra_gpu.h): there
//           is no catra_encode_free, so the context — not the caller — owns the
//           bytes. (The subtask's "caller frees" note is superseded by the
//           frozen 16-entry ABI + the C# NativeBridge, which has no free call.)
//
// KEYED MUTEX (ST-14/15 fix pattern): the input texture may be a shared interop
// resource co-owned with the D3D11<->DX12 pool, which carries an
// IDXGIKeyedMutex. Encode QI's the mutex and, when present, brackets the
// SubmitInput + synchronous QueryOutput with the consumer half of the ping-pong
// (interop_acquire before, interop_release after) so the AMF read never races
// the pool's next copy on the hardware. Textures we own outright expose no
// mutex (QI yields S_FALSE + null) and skip the guard.
//
// EXCEPTION SAFETY: AMF returns error codes (AMF_RESULT); every failure is
// mapped to a CATRA_ERR_* and logged. No exception escapes this TU (all state
// is RAII — AMF smart pointers + ComPtr + std::vector), and the extern "C"
// entry points that call in are wrapped in GuardCabi in catra_gpu.cpp, so a
// stray std::bad_alloc still surfaces as CATRA_ERR_UNKNOWN.
//
// THREADING: a single AmfEncoder is NOT safe for concurrent Encode calls (the
// AMF component and the staging buffer are per-context). The offline pipeline
// runs one encode job at a time.

#ifndef CATRA_ENCODE_AMF_H
#define CATRA_ENCODE_AMF_H

#include <cstdint>
#include <memory>

// Forward declarations only — keep this header free of <d3d12.h> / AMF headers
// so the C ABI header (catra_gpu.h) and the native test stay lightweight.
struct ID3D12Device;
struct ID3D12Resource;

namespace catra {

class AmfEncoder;

// True iff the bridge was compiled against the AMF SDK (CATRA_HAS_AMF defined).
// When false, every other AMF entry point degrades gracefully to NOT_IMPL.
bool AmfIsCompiled();

// HEVC tier selection (spec ST-16): Main tier for <= 4K30, High tier above that
// (4K55 / >4K). Pure + GPU-free so the native test can pin it directly, exactly
// like catra::SelectQualityMode for upscale. Returns an ABSTRACT tier code
// (0 = Main, 1 = High) decoupled from the AMF enum numbering; the real path
// maps it onto AMF_VIDEO_ENCODER_HEVC_TIER_* internally. The decision logic is
// mirrored in tests/native/test_encode.cpp.
int SelectHevcTier(int width, int height, double fps);

class AmfEncoder
{
public:
    AmfEncoder() = default;
    ~AmfEncoder();

    AmfEncoder(const AmfEncoder&) = delete;
    AmfEncoder& operator=(const AmfEncoder&) = delete;

    // Creates the AMF factory/context/component on the bridge's D3D12 device.
    // Returns a CATRA_* code:
    //   CATRA_OK              encoder ready
    //   CATRA_ERR_NOT_IMPL    built without the AMF SDK (CATRA_HAS_AMF off)
    //   CATRA_ERR_INVALID_ARG null device / non-positive dimensions / bitrate / fps
    //   CATRA_ERR_DEVICE      AMF runtime (amfrt64.dll) missing / factory /
    //                         context / component init failed (e.g. no AMD GPU)
    static int Create(ID3D12Device* device12,
                      int width, int height, int bitrateKbps, double fps,
                      std::unique_ptr<AmfEncoder>& out);

    // Encodes one DX12 NV12 texture. On success writes a pointer to the
    // context-owned packet buffer and its size in bytes. A size of 0 means the
    // encoder is still buffering (no packet ready yet) — NOT an error. The
    // pointer is valid until the next Encode/Flush on this context.
    int Encode(ID3D12Resource* texture, uint8_t** outBuf, int* outSize);

    // Drains the encoder (Drain) and concatenates every remaining packet into
    // the context-owned buffer. Same ownership/validity contract as Encode.
    // Called once at the end of the stream.
    int Flush(uint8_t** outBuf, int* outSize);

    int Width() const { return m_width; }
    int Height() const { return m_height; }

private:
    struct Impl;
    std::unique_ptr<Impl> m_impl;
    int m_width = 0;
    int m_height = 0;
};

} // namespace catra

#endif // CATRA_ENCODE_AMF_H

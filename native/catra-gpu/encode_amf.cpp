// encode_amf.cpp — AMF H.265 (HEVC) hardware encode backend (ST-16).
//
// Two compile modes, selected by CMake:
//
//   * CATRA_HAS_AMF defined (headers present via -DCATRA_AMF_ROOT):
//       real AMF implementation — factory/context/component, HEVC properties,
//       zero-copy DX12 surface wrap, SubmitInput/QueryOutput, Drain. The AMF
//       runtime (amfrt64.dll) is loaded DYNAMICALLY at Create time (it ships
//       with the AMD driver), so there is no link-time AMF dependency and a
//       missing runtime degrades to CATRA_ERR_DEVICE instead of a load failure.
//   * CATRA_HAS_AMF NOT defined (the default — the SDK is headers-only on
//       GPUOpen and never vendored): an inert stub. AmfIsCompiled() == false,
//       Create() == CATRA_ERR_NOT_IMPL. The C ABI surfaces NOT_IMPL gracefully.
//
// The AMF SDK is NOT downloaded by this repository. To build the real path,
// clone https://github.com/GPUOpen-LibrariesAndSDKs/AMF into lib/amf/
// (gitignored) and configure with -DCATRA_AMF_ROOT pointing at the repo root
// (the directory containing AMF/public/include/...). See scripts/build-native.ps1.
//
// BUFFER OWNERSHIP: encoded packets are copied into a context-owned staging
// vector; Encode/Flush return a pointer into it that is valid until the next
// Encode/Flush on the SAME context. The frozen C ABI (catra_gpu.h) has no
// catra_encode_free and the C# NativeBridge has no free call, so the context —
// not the caller — owns the bytes. This supersedes the subtask's "caller frees"
// note (see encode_amf.h for the full rationale).
//
// No exception escapes this TU: AMF returns AMF_RESULT codes (mapped to
// CATRA_ERR_*), all state is RAII, and the only throwing operation (the staging
// vector growing) is converted to CATRA_ERR_UNKNOWN by the GuardCabi wrapper in
// catra_gpu.cpp.

#include "encode_amf.h"
#include "catra_gpu.h"

namespace catra {
// Backend log shim defined in catra_gpu.cpp; forwards to the managed sink.
void BackendLog(int level, const char* fmt, ...);
}

// ---------------------------------------------------------------------------
// Pure, GPU-free HEVC tier selection — compiled in BOTH modes so the native
// test (tests/native/test_encode.cpp) can pin it directly, mirroring how
// test_upscale pins catra::SelectQualityMode. Decoupled from the AMF enum
// numbering on purpose: returns an abstract 0 = Main / 1 = High that the real
// path maps onto AMF_VIDEO_ENCODER_HEVC_TIER_* internally.
// ---------------------------------------------------------------------------
namespace catra {
namespace {
constexpr int kHevcTierMain = 0;
constexpr int kHevcTierHigh = 1;
} // namespace

int SelectHevcTier(int width, int height, double fps)
{
    if (width <= 0 || height <= 0)
    {
        return kHevcTierMain; // defensive default
    }
    const long long pixels = static_cast<long long>(width) * static_cast<long long>(height);
    const long long k4K = 3840LL * 2160LL;
    if (pixels > k4K)
    {
        return kHevcTierHigh; // above 4K -> High tier
    }
    if (pixels == k4K && fps > 30.5)
    {
        return kHevcTierHigh; // 4K55 -> High tier (4K30 stays Main)
    }
    return kHevcTierMain; // <= 4K30 -> Main tier
}

} // namespace catra

#if defined(CATRA_HAS_AMF)

#include "d3d_interop.h" // interop_acquire / interop_release (keyed mutex)

#include <d3d12.h>
#include <dxgi1_4.h>
#include <windows.h>
#include <wrl/client.h>

// AMF SDK (GPUOpen, headers-only). These resolve only when CATRA_AMF_ROOT is on
// the include path (CMake adds the repo root together with the CATRA_HAS_AMF
// define). Layout matches the GPUOpen AMF repository (AMF/public/include/...).
#include <AMF/public/include/core/Factory.h>
#include <AMF/public/include/core/Context.h>
#include <AMF/public/include/core/Component.h>
#include <AMF/public/include/core/Surface.h>
#include <AMF/public/include/core/Buffer.h>
#include <AMF/public/include/core/Data.h>
#include <AMF/public/include/components/VideoEncoderHEVC.h>

#include <cmath>
#include <cstdint>
#include <vector>

namespace catra {

namespace {

using Microsoft::WRL::ComPtr;

// Signature of the driver-exported factory creator (resolved by name from
// amfrt64.dll so we never link an AMF import lib).
using AmfCreateFactoryFn = AMF_RESULT(AMF_CDECL_CALL*)(amf_uint64 version,
                                                       AMFFactory** ppFactory);

// RAII guard for the CONSUMER half of the keyed-mutex ping-pong (ST-14/15 fix
// pattern — identical to upscale_fsr4.cpp). The shared input texture is co-owned
// with the interop pool, whose producer does Acquire(k)->copy->Release(k) with k
// alternating 0/1. The consumer must Acquire(k) before the AMF read and
// Release(k) after, on EVERY exit path, or a stuck key deadlocks the producer
// two frames later. Releases (and advances the consumer key) in the destructor;
// a no-op when the texture carried no keyed mutex (mutex == null / not held) —
// the case for textures we own outright.
struct KeyedMutexGuard
{
    IDXGIKeyedMutex* mutex = nullptr;
    uint64_t key = 0;
    uint64_t* nextKey = nullptr; // consumer key state advanced on release
    bool held = false;

    ~KeyedMutexGuard()
    {
        if (held && mutex != nullptr)
        {
            interop_release(mutex, key);
            if (nextKey != nullptr)
            {
                *nextKey ^= 1; // ping-pong: 0/1, matches the producer schedule
            }
        }
    }
};

} // namespace

// ---------------------------------------------------------------------------
// AmfEncoder::Impl
// ---------------------------------------------------------------------------

struct AmfEncoder::Impl
{
    HMODULE amfDll = nullptr; // amfrt64.dll (driver-provided); FreeLibrary'd last
    AMFFactoryPtr factory;
    AMFContextPtr context;
    AMFComponentPtr encoder;

    // Context-owned staging buffer for the most recent Encode packet (or the
    // concatenated Flush remainder). Returned pointers alias this vector and are
    // valid until the next Encode/Flush reassigns it.
    std::vector<uint8_t> packet;

    // Consumer half of the keyed-mutex ping-pong (ST-15). Alternates 0/1 per
    // consumed shared texture, in lockstep with the interop pool producer.
    uint64_t consumerKey = 0;

    bool drained = false; // set by Flush; Encode afterwards is a usage error

    ~Impl()
    {
        // AMF shutdown order: stop the component, then the context, then drop
        // the factory, then unload the runtime DLL (reverse of creation). The
        // smart pointers Release() their interfaces as they destruct; the
        // explicit Terminate() calls flush AMF's internal state first.
        if (encoder != nullptr)
        {
            encoder->Terminate();
        }
        if (context != nullptr)
        {
            context->Terminate();
        }
        // factory / context / encoder smart pointers release here (reverse
        // declaration order: encoder, context, factory).
        if (amfDll != nullptr)
        {
            FreeLibrary(amfDll);
            amfDll = nullptr;
        }
    }

    // Copies one AMF output buffer into the staging vector (replacing it) and
    // publishes the pointer/size. An empty buffer yields 0 bytes (not an error).
    int StagePacket(const AMFBufferPtr& buffer, uint8_t** outBuf, int* outSize)
    {
        const amf_size size = buffer->GetSize();
        const void* src = buffer->GetNative();
        if (size == 0 || src == nullptr)
        {
            packet.clear();
            *outBuf = nullptr;
            *outSize = 0;
            return CATRA_OK;
        }
        const uint8_t* begin = static_cast<const uint8_t*>(src);
        packet.assign(begin, begin + size); // may throw bad_alloc -> CATRA_ERR_UNKNOWN
        *outBuf = packet.data();
        *outSize = static_cast<int>(size);
        return CATRA_OK;
    }

    // Pulls a single output packet (if one is ready). Returns CATRA_OK with
    // 0 bytes when the encoder is still buffering (AMF_REPEAT / NEED_MORE_INPUT)
    // or already drained (AMF_EOF) — both are normal for the async encoder.
    int QueryOnce(uint8_t** outBuf, int* outSize)
    {
        AMFDataPtr data;
        const AMF_RESULT res = encoder->QueryOutput(&data);
        if (res == AMF_REPEAT || res == AMF_NEED_MORE_INPUT || res == AMF_EOF)
        {
            *outBuf = nullptr;
            *outSize = 0;
            return CATRA_OK; // buffering / drained — caller keeps going or stops
        }
        if (res != AMF_OK || data == nullptr)
        {
            BackendLog(CATRA_LOG_ERROR, "encode_amf: QueryOutput failed (res=%d)",
                       static_cast<int>(res));
            return CATRA_ERR_DEVICE;
        }
        AMFBufferPtr buffer(data); // QI; null if the output is not a buffer
        if (buffer == nullptr)
        {
            BackendLog(CATRA_LOG_ERROR, "encode_amf: QueryOutput produced a non-buffer");
            return CATRA_ERR_DEVICE;
        }
        return StagePacket(buffer, outBuf, outSize);
    }
};

AmfEncoder::~AmfEncoder() = default;

bool AmfIsCompiled()
{
    return true;
}

int AmfEncoder::Create(ID3D12Device* device12,
                       int width, int height, int bitrateKbps, double fps,
                       std::unique_ptr<AmfEncoder>& out)
{
    out.reset();

    if (device12 == nullptr || width <= 0 || height <= 0 || bitrateKbps <= 0 || fps <= 0.0)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "encode_amf: invalid create args (%dx%d, %d kbps, %.3f fps)",
                   width, height, bitrateKbps, fps);
        return CATRA_ERR_INVALID_ARG;
    }

    auto self = std::unique_ptr<AmfEncoder>(new AmfEncoder());
    self->m_width = width;
    self->m_height = height;
    self->m_impl = std::make_unique<Impl>();
    Impl& d = *self->m_impl;

    // --- 1. Load the AMF runtime (driver-provided) -------------------------
    // Dynamic load keeps the bridge link-clean and degrades gracefully when the
    // AMD driver (amfrt64.dll) is absent.
    d.amfDll = LoadLibraryW(AMF_DLL_NAME);
    if (d.amfDll == nullptr)
    {
        BackendLog(CATRA_LOG_WARN,
                   "encode_amf: AMF runtime (%ls) not found -> is the AMD Adrenalin "
                   "driver installed?", AMF_DLL_NAME);
        return CATRA_ERR_DEVICE;
    }
    auto createFactory = reinterpret_cast<AmfCreateFactoryFn>(
        GetProcAddress(d.amfDll, "AMFCreateFactory"));
    if (createFactory == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "encode_amf: AMFCreateFactory export missing");
        return CATRA_ERR_DEVICE;
    }

    // --- 2. Factory --------------------------------------------------------
    AMF_RESULT res = createFactory(AMF_VERSION, &d.factory);
    if (res != AMF_OK || d.factory == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "encode_amf: AMFCreateFactory failed (res=%d)",
                   static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }

    // --- 3. Context (init D3D12 on the bridge's shared device) -------------
    res = d.factory->CreateContext(&d.context);
    if (res != AMF_OK || d.context == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "encode_amf: CreateContext failed (res=%d)",
                   static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }
    res = d.context->InitDX12(device12);
    if (res != AMF_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "encode_amf: AMFContext::InitDX12 failed (res=%d) — needs an AMD "
                   "DX12 device with encode support", static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }

    // --- 4. Component (HEVC Main encoder) ----------------------------------
    res = d.factory->CreateComponent(d.context, AMFVideoEncoderUVD_H265_MAIN, &d.encoder);
    if (res != AMF_OK || d.encoder == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "encode_amf: CreateComponent(AMFVideoEncoderUVD_H265_MAIN) failed "
                   "(res=%d)", static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }

    // --- 5. Encoder properties ---------------------------------------------
    // Enum-valued properties are cast to amf_int64 to select the unambiguous
    // SetProperty overload (the AMF sample convention). FRAMESIZE/FRAMERATE use
    // the AMFSize/AMFRate overloads. A failing SetProperty is logged; a bad
    // configuration ultimately surfaces as an Init failure below.
    const AMFSize frameSize = {width, height};
    const AMFRate frameRate = {static_cast<amf_uint32>(std::lround(fps * 1000.0)), 1000};
    const amf_int64 targetBitrate = static_cast<amf_int64>(bitrateKbps) * 1000;
    const amf_int64 tier = (SelectHevcTier(width, height, fps) == kHevcTierHigh)
                               ? static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_TIER_HIGH)
                               : static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_TIER_MAIN);

    auto set = [&](const wchar_t* name, amf_int64 value, const char* what) {
        const AMF_RESULT r = d.encoder->SetProperty(name, value);
        if (r != AMF_OK)
        {
            BackendLog(CATRA_LOG_WARN, "encode_amf: SetProperty(%s) res=%d", what,
                       static_cast<int>(r));
        }
    };

    // Offline transcode preset: best quality, higher latency (acceptable — the
    // pipeline is offline; spec RF-03 prioritizes quality over speed).
    set(AMF_VIDEO_ENCODER_HEVC_USAGE,
        static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_USAGE_TRANSCONDING), "USAGE");
    set(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET,
        static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET_QUALITY), "QUALITY_PRESET");
    set(AMF_VIDEO_ENCODER_HEVC_PROFILE,
        static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_PROFILE_MAIN), "PROFILE");
    set(AMF_VIDEO_ENCODER_HEVC_TIER, tier, "TIER");
    // CBR at the requested target bitrate for predictable file sizes (profiles
    // carry 20 Mbps / 45 Mbps; spec ST-16).
    set(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD,
        static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD_CBR), "RATE_CONTROL");
    set(AMF_VIDEO_ENCODER_HEVC_TARGET_BITRATE, targetBitrate, "TARGET_BITRATE");
    // Insert VPS/SPS/PPS at every GOP boundary so the output is a self-contained
    // Annex B stream (ST-17 muxes it into MP4 via the hevc bitstream filter).
    set(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE,
        static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE_GOP_ALIGNED),
        "HEADER_INSERTION");
    set(AMF_VIDEO_ENCODER_HEVC_LOWLATENCY_MODE, 0, "LOWLATENCY");

    // Size/rate use their dedicated overloads (no enum ambiguity).
    res = d.encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FRAMESIZE, frameSize);
    if (res != AMF_OK)
    {
        BackendLog(CATRA_LOG_WARN, "encode_amf: SetProperty(FRAMESIZE) res=%d",
                   static_cast<int>(res));
    }
    res = d.encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FRAMERATE, frameRate);
    if (res != AMF_OK)
    {
        BackendLog(CATRA_LOG_WARN, "encode_amf: SetProperty(FRAMERATE) res=%d",
                   static_cast<int>(res));
    }

    // --- 6. Init (input format NV12; the pipeline feeds NV12 frames) -------
    res = d.encoder->Init(AMF_SURFACE_NV12, width, height);
    if (res != AMF_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "encode_amf: encoder Init(%dx%d NV12) failed (res=%d)", width, height,
                   static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }

    BackendLog(CATRA_LOG_INFO,
               "encode_amf: HEVC encoder ready %dx%d @ %.3f fps, %d kbps (tier=%s)",
               width, height, fps, bitrateKbps,
               (tier == static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_TIER_HIGH)) ? "High" : "Main");

    out = std::move(self);
    return CATRA_OK;
}

int AmfEncoder::Encode(ID3D12Resource* texture, uint8_t** outBuf, int* outSize)
{
    if (outBuf != nullptr)
    {
        *outBuf = nullptr;
    }
    if (outSize != nullptr)
    {
        *outSize = 0;
    }
    if (m_impl == nullptr || texture == nullptr || outBuf == nullptr || outSize == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    Impl& d = *m_impl;
    if (d.drained)
    {
        BackendLog(CATRA_LOG_ERROR, "encode_amf: Encode after Flush is not allowed");
        return CATRA_ERR_INVALID_ARG;
    }

    // --- Keyed-mutex consumer acquire (ST-15 ping-pong) --------------------
    // QI the keyed mutex off the (possibly shared) input texture. S_FALSE + null
    // when the texture is ours outright -> skip. Declared BEFORE the guard so the
    // ComPtr outlives the guard's release in the destructor order.
    ComPtr<IDXGIKeyedMutex> keyedMutex;
    texture->QueryInterface(IID_PPV_ARGS(keyedMutex.GetAddressOf()));

    KeyedMutexGuard mutexGuard;
    mutexGuard.nextKey = &d.consumerKey;
    mutexGuard.key = d.consumerKey;
    if (keyedMutex != nullptr)
    {
        const int arc = interop_acquire(keyedMutex.Get(), mutexGuard.key, 5000);
        if (arc != CATRA_OK)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "encode_amf: keyed-mutex acquire rc=%d (key=%llu)", arc,
                       static_cast<unsigned long long>(mutexGuard.key));
            return CATRA_ERR_DEVICE;
        }
        mutexGuard.mutex = keyedMutex.Get();
        mutexGuard.held = true; // destructor releases on every exit from here
    }

    // --- Wrap the DX12 texture zero-copy as an AMF surface -----------------
    // The resource already lives on the AMF D3D12 device, so no GPU copy is
    // needed; AMF's encoder holds the surface reference in its internal input
    // queue (the 4-8 deep "surface pool" under USAGE_TRANSCONDING).
    AMFSurfacePtr surface;
    AMF_RESULT res = d.context->CreateSurfaceFromDX12Native(texture, &surface, nullptr);
    if (res != AMF_OK || surface == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "encode_amf: CreateSurfaceFromDX12Native failed (res=%d)",
                   static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }

    // --- Submit + query one output -----------------------------------------
    // AMF_INPUT_FULL (input queue full) cannot occur in this synchronous usage
    // because we drain one packet per submit; treat it as a device fault.
    res = d.encoder->SubmitInput(surface);
    if (res != AMF_OK)
    {
        // Includes AMF_INPUT_FULL (input queue full): impossible in this
        // synchronous one-drain-per-submit usage, so any failure is a device fault.
        BackendLog(CATRA_LOG_ERROR, "encode_amf: SubmitInput failed (res=%d)",
                   static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }

    // The keyed mutex stays held across the synchronous QueryOutput: once a
    // packet is produced the input frame has been consumed on the GPU timeline,
    // so releasing afterwards is safe (mirrors the FSR 4 dispatch guard).
    return d.QueryOnce(outBuf, outSize);
}

int AmfEncoder::Flush(uint8_t** outBuf, int* outSize)
{
    if (outBuf != nullptr)
    {
        *outBuf = nullptr;
    }
    if (outSize != nullptr)
    {
        *outSize = 0;
    }
    if (m_impl == nullptr || outBuf == nullptr || outSize == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    Impl& d = *m_impl;
    if (d.drained)
    {
        // Idempotent: a second flush yields nothing.
        return CATRA_OK;
    }

    AMF_RESULT res = d.encoder->Drain();
    if (res != AMF_OK)
    {
        BackendLog(CATRA_LOG_ERROR, "encode_amf: Drain failed (res=%d)", static_cast<int>(res));
        return CATRA_ERR_DEVICE;
    }
    d.drained = true;

    // Collect every remaining packet, concatenated, until AMF_EOF. After Drain
    // the encoder guarantees forward progress to EOF (no infinite REPEAT).
    d.packet.clear();
    for (;;)
    {
        AMFDataPtr data;
        res = d.encoder->QueryOutput(&data);
        if (res == AMF_EOF)
        {
            break;
        }
        if (res == AMF_REPEAT || res == AMF_NEED_MORE_INPUT)
        {
            continue;
        }
        if (res != AMF_OK || data == nullptr)
        {
            BackendLog(CATRA_LOG_ERROR, "encode_amf: flush QueryOutput failed (res=%d)",
                       static_cast<int>(res));
            return CATRA_ERR_DEVICE;
        }
        AMFBufferPtr buffer(data);
        if (buffer == nullptr)
        {
            continue;
        }
        const amf_size size = buffer->GetSize();
        const void* src = buffer->GetNative();
        if (size == 0 || src == nullptr)
        {
            continue;
        }
        const uint8_t* begin = static_cast<const uint8_t*>(src);
        d.packet.insert(d.packet.end(), begin, begin + size); // may throw -> ERR_UNKNOWN
    }

    if (d.packet.empty())
    {
        *outBuf = nullptr;
        *outSize = 0;
        return CATRA_OK;
    }
    *outBuf = d.packet.data();
    *outSize = static_cast<int>(d.packet.size());
    return CATRA_OK;
}

} // namespace catra

#else // !CATRA_HAS_AMF -----------------------------------------------------
//
// Inert stub: the AMF SDK is headers-only on GPUOpen and never vendored. Every
// entry point degrades gracefully so the bridge builds and loads; the C ABI
// surfaces CATRA_ERR_NOT_IMPL (spec: "sem headers -> NOT_IMPL gracioso").

namespace catra {

// The class holds a std::unique_ptr<Impl>; the defaulted destructor must see a
// COMPLETE Impl even in the stub build. The stub never populates m_impl, so an
// empty definition suffices.
struct AmfEncoder::Impl {};

bool AmfIsCompiled()
{
    return false;
}

int AmfEncoder::Create(ID3D12Device*, int, int, int, double,
                       std::unique_ptr<AmfEncoder>& out)
{
    out.reset();
    BackendLog(CATRA_LOG_WARN,
               "encode_amf: built without the AMF SDK (CATRA_HAS_AMF undefined) "
               "-> pass -DCATRA_AMF_ROOT=<AMF repo root> to enable H.265 encode");
    return CATRA_ERR_NOT_IMPL;
}

int AmfEncoder::Encode(ID3D12Resource*, uint8_t** outBuf, int* outSize)
{
    if (outBuf != nullptr)
    {
        *outBuf = nullptr;
    }
    if (outSize != nullptr)
    {
        *outSize = 0;
    }
    return CATRA_ERR_NOT_IMPL;
}

int AmfEncoder::Flush(uint8_t** outBuf, int* outSize)
{
    if (outBuf != nullptr)
    {
        *outBuf = nullptr;
    }
    if (outSize != nullptr)
    {
        *outSize = 0;
    }
    return CATRA_ERR_NOT_IMPL;
}

AmfEncoder::~AmfEncoder() = default;

} // namespace catra

#endif // CATRA_HAS_AMF

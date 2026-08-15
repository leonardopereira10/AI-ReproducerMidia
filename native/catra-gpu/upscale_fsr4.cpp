// upscale_fsr4.cpp — FSR 4/3.1 temporal upscale backend (ST-14, SPRINT_04
// subtask 02). See upscale_fsr4.h for the contract.
//
// DESIGN NOTES
//   * Runtime-loaded via the FFX API 2.x flat C surface — NEVER a build-time
//     SDK dependency. catra::ffx::FfxRuntime (ffx_runtime.h, subtask 01) loads
//     amd_fidelityfx_loader_dx12.dll and resolves ffxCreateContext /
//     ffxDestroyContext / ffxConfigure / ffxQuery / ffxDispatch. Only the
//     vendored MIT headers (lib/FidelityFX-SDK-2.3.0) are included.
//   * The old compile-gate (CATRA_HAS_FSR4 / CATRA_FSR_SDK_ROOT) is gone:
//     this TU always compiles; availability is decided at runtime by
//     Fsr4IsAvailable (loader + 8 DLLs + throw-away context probe).
//   * VIDEO MODE (zero-MV): the FFX upscale effect is temporal and wants
//     engine motion vectors / depth / jitter. CATRA feeds persistent ZEROED
//     dummies (MV R32G32_FLOAT, depth R32_FLOAT, both srcW x srcH, cleared
//     once on the first dispatch) + jitterOffset (0,0). reset=true on the
//     first frame and after RequestSceneCut() (catra_upscale_reset, PO
//     decision D-PO-1). Known quality limitation: ghosting across cuts until
//     a reset is consumed.
//   * OUTPUT FORMAT (PO decision D-PO-4): B8G8R8A8_UNORM (BGRA), aligning
//     with FSR 1 (upscale_fsr1.cpp) and AMF_SURFACE_BGRA; RGBA was rejected
//     (docs/FSR_AMF_ISSUE_CONTEXT.md). The --fsr4-smoke native test validates
//     this.
//   * The DX12 device comes from catra::CreateD3D12Device (the ST-15 interop
//     seam: the shared bridge device once interop_init has run). Process
//     obtains the input via catra::ShareTexture and brackets the dispatch with
//     the consumer half of the keyed-mutex ping-pong (KeyedMutexGuard below),
//     including the pool-rebuild resync on interop_pool_generation().
//   * Per-context log bridge: subtask 01 found that ffxConfigure(GlobalDebug)
//     on a NULL context returns ret=2 (ERROR_UNKNOWN_DESCTYPE) on this loader
//     (non-fatal). Following the reference-sample pattern, the log sink is
//     installed PER CONTEXT here: fpMessage in the create descriptor +
//     best-effort Configure(GlobalDebug) on the created context.
//   * No exception escapes this TU; every ffx failure maps to CATRA_ERR_*.

#include "upscale_fsr4.h"
#include "d3d_interop.h"
#include "ffx_runtime.h" // includes ffx_api.h / ffx_api_loader.h / ffx_upscale.h (+ _WINDOWS gotcha)
#include "catra_gpu.h"

#include <dx12/ffx_api_dx12.h> // ffxCreateBackendDX12Desc + ffxApiGetResourceDX12 (C++)

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

namespace catra {
void BackendLog(int level, const char* fmt, ...);
}

namespace catra {

namespace {

using Microsoft::WRL::ComPtr;

// Fence wait cap for the post-dispatch GPU sync. Replaces the stub's INFINITE:
// a wedged queue must surface CATRA_ERR_DEVICE, not hang the pipeline thread.
constexpr DWORD kFenceWaitTimeoutMs = 10000;

// Dummy per-frame metadata for the temporal effect (video has none of these):
// frameTimeDelta is a nominal 60 fps frame in MILLISECONDS (the FFX contract);
// the camera block is inert but must be plausible (the runtime derives
// velocity heuristics from it). All documented dummies, zero-MV mode.
constexpr float kFrameTimeDeltaMs = 16.6667f;
constexpr float kCameraNear = 0.1f;
constexpr float kCameraFar = 1000.0f;
constexpr float kCameraFovAngleVertical = 1.0472f; // ~60 degrees
constexpr float kViewSpaceToMetersFactor = 1.0f;

// FFX -> BackendLog message sink, installed PER CONTEXT via
// ffxCreateContextDescUpscale::fpMessage (and best-effort GlobalDebug
// Configure). Invoked from FFX threads: stack buffer only, no allocation,
// no exceptions. catra::BackendLog is thread-safe (g_logMutex).
void Fsr4MessageSink(uint32_t type, const wchar_t* message)
{
    char buffer[1024];
    buffer[0] = '\0';
    if (message != nullptr)
    {
        WideCharToMultiByte(CP_UTF8, 0, message, -1, buffer,
                            static_cast<int>(sizeof(buffer)), nullptr, nullptr);
        buffer[sizeof(buffer) - 1] = '\0';
    }

    int level = CATRA_LOG_INFO;
    if (type == FFX_API_MESSAGE_TYPE_ERROR)
    {
        level = CATRA_LOG_ERROR;
    }
    else if (type == FFX_API_MESSAGE_TYPE_WARNING)
    {
        level = CATRA_LOG_WARN;
    }
    BackendLog(level, "[FFX-upscale] %s", buffer);
}

// RAII guard for the CONSUMER half of the keyed-mutex ping-pong (ST-15). The
// shared input resource is co-owned with the interop pool, whose producer does
// Acquire(k)->copy->Release(k) with k alternating 0/1 (g_frameKey). The
// consumer must Acquire(k) before reading and Release(k) after, on EVERY exit
// path, or a stuck key deadlocks the producer two frames later. Releases (and
// advances the consumer key) in the destructor; no-op when the resource carried
// no keyed mutex (mutex == null / not held) — the case for textures we own.
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
// Fsr4Upscaler::Impl
// ---------------------------------------------------------------------------

struct Fsr4Upscaler::Impl
{
    ID3D11Device* d3d11Device = nullptr; // borrowed (bridge-owned)
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> cmdList;
    ComPtr<ID3D12Fence> fence;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;

    // Opaque handle of the flat C API (ffxCreateContext). NOT the old C++
    // wrapper type. Destroyed via ffxDestroyContext in ~Fsr4Upscaler.
    ffxContext upscaleCtx = nullptr;

    // Kept for logging/diagnostics only: the FFX 2.x create descriptor has NO
    // qualityMode field (presets became a resolution choice on the caller
    // side); do not attempt to validate the ratio against it.
    UpscaleQualityMode quality = UpscaleQualityMode::Quality;

    // --- zero-MV dummies (persistent, cleared once on first dispatch) ------
    // motionVectors: RG32F at render (src) size, zeroed.
    ComPtr<ID3D12Resource> mvTex;
    // depth: R32F at render (src) size, zeroed.
    ComPtr<ID3D12Resource> depthTex;
    // Shader-visible CBV_SRV_UAV heap with one UAV slot per dummy — the
    // ClearUnorderedAccessViewFloat path needs GPU-visible descriptors. The
    // FFX runtime manages its own heaps for the dispatch itself.
    ComPtr<ID3D12DescriptorHeap> dummyHeap;
    // CPU-only twin of dummyHeap (same UAVs). ClearUnorderedAccessViewFloat
    // reads the descriptor through its CPU handle, and the runtime only allows
    // that from a CPU-only heap (validation id 646 on shader-visible heaps).
    ComPtr<ID3D12DescriptorHeap> dummyHeapCpu;
    bool dummiesZeroed = false;

    // Consumed by the next dispatch as ffxDispatchDescUpscale::reset. True at
    // create (the first frame always resets) and after RequestSceneCut().
    bool pendingReset = true;

    // Consumer half of the keyed-mutex ping-pong (ST-15). Starts at 0 and
    // alternates 0/1 per consumed frame, in lockstep with the pool producer's
    // g_frameKey, so Acquire(k) always waits on the matching Release(k).
    uint64_t consumerKey = 0;

    // Last interop pool generation observed (d3d_interop). A pool rebuild
    // (resolution/format change) mints fresh keyed mutexes and resets the
    // producer key to 0; when the generation changes the consumer must reset
    // consumerKey to 0 in lockstep, or its AcquireSync waits for a Release
    // the rebuilt producer never issues (5 s timeout).
    uint64_t lastPoolGeneration = 0;
};

Fsr4Upscaler::~Fsr4Upscaler()
{
    if (m_impl != nullptr)
    {
        Impl& d = *m_impl;
        if (d.upscaleCtx != nullptr)
        {
            // Only while the loader is still mapped: after FfxRuntime::Unload
            // the function pointers dangle (catra_shutdown ordering destroys
            // upscale contexts before the Unload wiring, so this is the norm).
            if (catra::ffx::FfxRuntime::IsLoaded())
            {
                catra::ffx::FfxRuntime::Functions().DestroyContext(&d.upscaleCtx, nullptr);
            }
            d.upscaleCtx = nullptr;
        }
        if (d.fenceEvent != nullptr)
        {
            CloseHandle(d.fenceEvent);
            d.fenceEvent = nullptr;
        }
        // ComPtr members (device/queue/command objects, dummies, heap)
        // release themselves in reverse declaration order.
    }
}

bool Fsr4IsAvailable(ID3D11Device* d3d11Device)
{
    if (d3d11Device == nullptr)
    {
        return false;
    }

    // 1. FFX loader (idempotent): loader DLL + all 5 ffx exports.
    if (!catra::ffx::FfxRuntime::Load())
    {
        BackendLog(CATRA_LOG_INFO,
                   "upscale_fsr4: FFX loader not loadable -> FSR 4 off");
        return false;
    }

    // 2. The 8 FFX runtime DLLs (list A1) next to this module.
    if (!catra::ffx::FfxRuntime::ProbeDependencyDlls())
    {
        BackendLog(CATRA_LOG_INFO,
                   "upscale_fsr4: FFX runtime DLLs incomplete -> FSR 4 off");
        return false;
    }

    // NO vendor/device-ID pre-filter here (the old RDNA 4 heuristic is gone):
    // the FFX runtime selects FSR 4 ML on RDNA 4 and the FSR 3.1 fallback on
    // other adapters by itself. The definitive check is the context probe.

    // 3. Definitive probe: throw-away 64x64 -> 128x128 context through the
    //    same Create path (CreateD3D12Device = ST-15 interop seam).
    std::unique_ptr<Fsr4Upscaler> probe;
    const int rc = Fsr4Upscaler::Create(d3d11Device, 64, 64, 128, 128,
                                        UpscaleQualityMode::Quality, probe);
    if (rc != CATRA_OK)
    {
        BackendLog(CATRA_LOG_INFO,
                   "upscale_fsr4: probe context create rc=%d -> FSR 4 unavailable",
                   rc);
        return false;
    }
    BackendLog(CATRA_LOG_INFO,
               "upscale_fsr4: available (FFX runtime-loaded; provider chosen by the runtime)");
    return true;
}

int Fsr4Upscaler::Create(ID3D11Device* d3d11Device,
                         int srcW, int srcH, int dstW, int dstH,
                         UpscaleQualityMode quality,
                         std::unique_ptr<Fsr4Upscaler>& out)
{
    out.reset();

    if (d3d11Device == nullptr || srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: invalid create args (%dx%d -> %dx%d)",
                   srcW, srcH, dstW, dstH);
        return CATRA_ERR_INVALID_ARG;
    }

    // Runtime gate (also covered by Fsr4IsAvailable, but Create can be called
    // directly by tests): loader + the 8 dependency DLLs must be present.
    if (!catra::ffx::FfxRuntime::Load() || !catra::ffx::FfxRuntime::ProbeDependencyDlls())
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr4: FFX runtime unavailable (loader or dependency DLLs)");
        return CATRA_ERR_DEVICE;
    }

    auto self = std::unique_ptr<Fsr4Upscaler>(new Fsr4Upscaler());
    self->m_srcW = srcW;
    self->m_srcH = srcH;
    self->m_dstW = dstW;
    self->m_dstH = dstH;
    self->m_impl = std::make_unique<Impl>();
    Impl& d = *self->m_impl;
    d.d3d11Device = d3d11Device;
    d.quality = quality;

    // --- D3D12 device on the bridge's adapter (ST-15 interop seam) ---------
    HRESULT hr = CreateD3D12Device(d3d11Device, d.device.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr4: CreateD3D12Device hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return (hr == E_NOTIMPL) ? CATRA_ERR_NOT_IMPL : CATRA_ERR_DEVICE;
    }

    // --- Command plumbing (the FFX dispatch records into OUR list) ----------
    D3D12_COMMAND_QUEUE_DESC queueDesc = {};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    hr = d.device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(d.queue.GetAddressOf()));
    if (SUCCEEDED(hr))
    {
        hr = d.device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(d.allocator.GetAddressOf()));
    }
    if (SUCCEEDED(hr))
    {
        hr = d.device->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_COMPUTE, d.allocator.Get(), nullptr,
            IID_PPV_ARGS(d.cmdList.GetAddressOf()));
    }
    if (SUCCEEDED(hr))
    {
        d.cmdList->Close();
        hr = d.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(d.fence.GetAddressOf()));
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: command objects hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    d.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (d.fenceEvent == nullptr)
    {
        return CATRA_ERR_DEVICE;
    }

    // --- zero-MV dummy textures (persistent, UAV-capable) -----------------
    // NOTE: no D3D12 optimized clear value here — the runtime rejects
    // pOptimizedClearValue on ALLOW_UNORDERED_ACCESS textures without an RTV/
    // DSV flag (validation id 815). The first-dispatch ClearUnorderedAccess-
    // ViewFloat pass zeroes both textures instead.
    D3D12_HEAP_PROPERTIES defaultHeap = {};
    defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;

    D3D12_RESOURCE_DESC mvDesc = {};
    mvDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    mvDesc.Width = static_cast<UINT>(srcW);
    mvDesc.Height = static_cast<UINT>(srcH);
    mvDesc.DepthOrArraySize = 1;
    mvDesc.MipLevels = 1;
    mvDesc.Format = DXGI_FORMAT_R32G32_FLOAT;
    mvDesc.SampleDesc.Count = 1;
    mvDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    hr = d.device->CreateCommittedResource(
        &defaultHeap, D3D12_HEAP_FLAG_NONE, &mvDesc,
        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
        IID_PPV_ARGS(d.mvTex.GetAddressOf()));
    if (SUCCEEDED(hr))
    {
        D3D12_RESOURCE_DESC depthDesc = mvDesc;
        depthDesc.Format = DXGI_FORMAT_R32_FLOAT;
        hr = d.device->CreateCommittedResource(
            &defaultHeap, D3D12_HEAP_FLAG_NONE, &depthDesc,
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
            IID_PPV_ARGS(d.depthTex.GetAddressOf()));
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: dummy MV/depth texture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // Shader-visible UAV heap (GPU handle side of the clear) + CPU-only twin
    // (CPU handle side — validation id 646 forbids reading descriptors from a
    // shader-visible heap).
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    heapDesc.NumDescriptors = 2;
    heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    hr = d.device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(d.dummyHeap.GetAddressOf()));
    if (SUCCEEDED(hr))
    {
        D3D12_DESCRIPTOR_HEAP_DESC cpuHeapDesc = heapDesc;
        cpuHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE; // CPU-only
        hr = d.device->CreateDescriptorHeap(&cpuHeapDesc, IID_PPV_ARGS(d.dummyHeapCpu.GetAddressOf()));
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: dummy UAV heap hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    const UINT inc = d.device->GetDescriptorHandleIncrementSize(
        D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    const D3D12_CPU_DESCRIPTOR_HANDLE cpu0 = d.dummyHeap->GetCPUDescriptorHandleForHeapStart();
    const D3D12_CPU_DESCRIPTOR_HANDLE cpu1 = {cpu0.ptr + inc};
    d.device->CreateUnorderedAccessView(d.mvTex.Get(), nullptr, nullptr, cpu0);
    d.device->CreateUnorderedAccessView(d.depthTex.Get(), nullptr, nullptr, cpu1);
    const D3D12_CPU_DESCRIPTOR_HANDLE cpuOnly0 = d.dummyHeapCpu->GetCPUDescriptorHandleForHeapStart();
    const D3D12_CPU_DESCRIPTOR_HANDLE cpuOnly1 = {cpuOnly0.ptr + inc};
    d.device->CreateUnorderedAccessView(d.mvTex.Get(), nullptr, nullptr, cpuOnly0);
    d.device->CreateUnorderedAccessView(d.depthTex.Get(), nullptr, nullptr, cpuOnly1);

    // --- FFX upscale context (flat C descriptor chain, reference-sample order:
    //     createFsr -> backendDesc -> headerVersion; lifetimes must span the
    //     CreateContext call — fsrapirendermodule.cpp ~948-1040) -------------
    ffxCreateContextDescUpscale desc = {};
    desc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    // Video input is gamma-encoded (BT.709/sRGB-like), not linear HDR.
    desc.flags = FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE;
    desc.maxRenderSize.width = static_cast<uint32_t>(srcW);
    desc.maxRenderSize.height = static_cast<uint32_t>(srcH);
    desc.maxUpscaleSize.width = static_cast<uint32_t>(dstW);
    desc.maxUpscaleSize.height = static_cast<uint32_t>(dstH);
    desc.fpMessage = &Fsr4MessageSink; // per-context log channel

    ffxCreateBackendDX12Desc backend = {};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = d.device.Get();

    ffxCreateContextDescUpscaleVersion version = {};
    version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
    version.version = FFX_UPSCALER_VERSION; // no provider selection this sprint

    desc.header.pNext = &backend.header;
    backend.header.pNext = &version.header;

    const ffxReturnCode_t rc =
        catra::ffx::FfxRuntime::Functions().CreateContext(&d.upscaleCtx, &desc.header, nullptr);
    if (rc != FFX_API_RETURN_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "upscale_fsr4: ffxCreateContext failed (ret=%u) %dx%d -> %dx%d",
                   static_cast<unsigned>(rc), srcW, srcH, dstW, dstH);
        return CATRA_ERR_DEVICE;
    }

    // Per-context log bridge, best effort (non-fatal). Subtask 01 known issue:
    // the NULL-context GlobalDebug Configure returns ret=2
    // (ERROR_UNKNOWN_DESCTYPE) on this loader, so the bridge is applied PER
    // CONTEXT instead (reference-sample pattern). fpMessage above already
    // captures context messages; this additionally routes the runtime's debug
    // checker output. Try the modern descriptor first, then the legacy one.
    ffxConfigureDescGlobalDebug cfg = {};
    cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG;
    cfg.effectId = FFX_API_EFFECT_ID_GENERAL;
    cfg.fpMessage = &Fsr4MessageSink;
    cfg.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;
    ffxReturnCode_t cfgRc =
        catra::ffx::FfxRuntime::Functions().Configure(&d.upscaleCtx, &cfg.header);
    if (cfgRc != FFX_API_RETURN_OK)
    {
        ffxConfigureDescGlobalDebug1 cfg1 = {};
        cfg1.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG1;
        cfg1.fpMessage = &Fsr4MessageSink;
        cfg1.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;
        cfgRc = catra::ffx::FfxRuntime::Functions().Configure(&d.upscaleCtx, &cfg1.header);
    }
    if (cfgRc != FFX_API_RETURN_OK)
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr4: per-context log bridge not installed (ret=%u, non-fatal)",
                   static_cast<unsigned>(cfgRc));
    }

    BackendLog(CATRA_LOG_INFO,
               "upscale_fsr4: context ready %dx%d -> %dx%d (quality=%d, zero-MV video mode)",
               srcW, srcH, dstW, dstH, static_cast<int>(quality));

    out = std::move(self);
    return CATRA_OK;
}

int Fsr4Upscaler::Process(ID3D11Texture2D* src, ID3D12Resource** outDst)
{
    if (outDst != nullptr)
    {
        *outDst = nullptr;
    }
    if (m_impl == nullptr || src == nullptr || outDst == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    Impl& d = *m_impl;
    if (d.upscaleCtx == nullptr || !catra::ffx::FfxRuntime::IsLoaded())
    {
        return CATRA_ERR_DEVICE;
    }

    // --- D3D11 -> DX12 shared input (ST-15 interop) ------------------------
    // Pooled GPU-GPU copy (or zero-copy NT share); the returned resource is
    // co-owned with the interop pool and carries a keyed mutex we must honor.
    ComPtr<ID3D12Resource> srcRes;
    HRESULT hr = ShareTexture(d.d3d11Device, d.device.Get(), src, srcRes.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr4: ShareTexture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return (hr == E_NOTIMPL) ? CATRA_ERR_NOT_IMPL : CATRA_ERR_DEVICE;
    }

    // --- Keyed-mutex consumer acquire (ST-15 ping-pong) --------------------
    // QI the keyed mutex off the shared resource (S_FALSE + null when the
    // resource is ours outright -> skip). AcquireSync blocks until the pool
    // producer's matching Release(key) completes on the GPU timeline; a
    // timeout/failure is a device fault (CATRA_ERR_DEVICE).
    KeyedMutexGuard mutexGuard;
    mutexGuard.nextKey = &d.consumerKey;
    // QI the keyed mutex (raw COM, codebase pattern from encode_amf.cpp).
    // Sets null on E_NOINTERFACE (resource owned outright) -> guard skips.
    srcRes->QueryInterface(IID_PPV_ARGS(&mutexGuard.mutex));
    if (mutexGuard.mutex != nullptr)
    {
        // Pool-rebuild resync (see Impl::lastPoolGeneration): align the
        // consumer key with the rebuilt producer BEFORE acquiring.
        const uint64_t generation = interop_pool_generation();
        if (generation != d.lastPoolGeneration)
        {
            d.consumerKey = 0;
            d.lastPoolGeneration = generation;
        }
        mutexGuard.key = d.consumerKey;
        const int arc = interop_acquire(mutexGuard.mutex, mutexGuard.key, 5000);
        if (arc != CATRA_OK)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "upscale_fsr4: keyed-mutex acquire rc=%d (key=%llu)",
                       arc, static_cast<unsigned long long>(mutexGuard.key));
            return CATRA_ERR_DEVICE;
        }
        mutexGuard.held = true; // destructor releases on every exit from here
    }

    // --- Output texture (dstW x dstH, PER FRAME — caller gets a fresh one) -
    // BGRA per PO decision D-PO-4 (aligns with FSR 1 + AMF_SURFACE_BGRA).
    // ALLOW_UNORDERED_ACCESS is mandatory: ffxApiGetResourceDX12 only infers
    // the UAV usage from that flag, and the effect writes via UAV.
    D3D12_RESOURCE_DESC outDesc = {};
    outDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    outDesc.Width = static_cast<UINT>(m_dstW);
    outDesc.Height = static_cast<UINT>(m_dstH);
    outDesc.DepthOrArraySize = 1;
    outDesc.MipLevels = 1;
    outDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    outDesc.SampleDesc.Count = 1;
    outDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    D3D12_HEAP_PROPERTIES defaultHeap = {};
    defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;

    ComPtr<ID3D12Resource> dstRes;
    hr = d.device->CreateCommittedResource(
        &defaultHeap, D3D12_HEAP_FLAG_NONE, &outDesc,
        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
        IID_PPV_ARGS(dstRes.GetAddressOf()));
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: output texture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Record: reset the list, then zero the dummies once -----------------
    hr = d.allocator->Reset();
    if (SUCCEEDED(hr))
    {
        hr = d.cmdList->Reset(d.allocator.Get(), nullptr);
    }
    if (FAILED(hr))
    {
        return CATRA_ERR_DEVICE;
    }

    if (!d.dummiesZeroed)
    {
        // Both dummies are created in UNORDERED_ACCESS and stay in it (the
        // dispatch declares them UNORDERED_ACCESS too), so no barriers are
        // needed around the clear. ClearUnorderedAccessViewFloat requires the
        // shader-visible heap bound at record time; the CPU handle comes from
        // the CPU-only twin heap (the runtime reads the descriptor there).
        ID3D12DescriptorHeap* heaps[] = {d.dummyHeap.Get()};
        d.cmdList->SetDescriptorHeaps(1, heaps);
        const UINT inc = d.device->GetDescriptorHandleIncrementSize(
            D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        const D3D12_GPU_DESCRIPTOR_HANDLE gpu0 =
            d.dummyHeap->GetGPUDescriptorHandleForHeapStart();
        const D3D12_GPU_DESCRIPTOR_HANDLE gpu1 = {gpu0.ptr + inc};
        const D3D12_CPU_DESCRIPTOR_HANDLE cpuOnly0 =
            d.dummyHeapCpu->GetCPUDescriptorHandleForHeapStart();
        const D3D12_CPU_DESCRIPTOR_HANDLE cpuOnly1 = {cpuOnly0.ptr + inc};
        const float zero4[4] = {0.0f, 0.0f, 0.0f, 0.0f};
        d.cmdList->ClearUnorderedAccessViewFloat(gpu0, cpuOnly0, d.mvTex.Get(), zero4, 0, nullptr);
        d.cmdList->ClearUnorderedAccessViewFloat(gpu1, cpuOnly1, d.depthTex.Get(), zero4, 0, nullptr);
        d.dummiesZeroed = true;
    }

    // --- Fill + dispatch the FFX upscale effect (zero-MV video mode) --------
    ffxDispatchDescUpscale dispatch = {};
    dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    dispatch.commandList = d.cmdList.Get(); // flat C: raw pointer, no helper
    // NT-handle-opened resources start in COMMON; the FFX runtime records the
    // input barriers itself. Dummies/output are declared in UAV (their real
    // state — created UAV and never transitioned out by this backend).
    dispatch.color = ffxApiGetResourceDX12(srcRes.Get(), FFX_API_RESOURCE_STATE_COMMON);
    dispatch.depth = ffxApiGetResourceDX12(d.depthTex.Get(),
                                           FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    dispatch.motionVectors = ffxApiGetResourceDX12(d.mvTex.Get(),
                                                   FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    dispatch.exposure = FfxApiResource{};                  // optional, absent
    dispatch.reactive = FfxApiResource{};                  // optional, absent
    dispatch.transparencyAndComposition = FfxApiResource{}; // optional, absent
    dispatch.output = ffxApiGetResourceDX12(dstRes.Get(),
                                            FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    dispatch.jitterOffset.x = 0.0f; // video: no engine jitter
    dispatch.jitterOffset.y = 0.0f;
    dispatch.motionVectorScale.x = static_cast<float>(m_srcW);
    dispatch.motionVectorScale.y = static_cast<float>(m_srcH);
    dispatch.renderSize.width = static_cast<uint32_t>(m_srcW);
    dispatch.renderSize.height = static_cast<uint32_t>(m_srcH);
    dispatch.upscaleSize.width = static_cast<uint32_t>(m_dstW);
    dispatch.upscaleSize.height = static_cast<uint32_t>(m_dstH);
    dispatch.enableSharpening = false; // tunable in QA; default off
    dispatch.sharpness = 0.0f;
    // Dummy frame time (ms): the backend has no fps signal; the temporal
    // heuristics only need a plausible constant.
    dispatch.frameTimeDelta = kFrameTimeDeltaMs;
    dispatch.preExposure = 1.0f;
    dispatch.reset = d.pendingReset; // first frame + after RequestSceneCut()
    d.pendingReset = false;          // consumed
    // Camera block: documented dummies — video has no camera; the runtime's
    // velocity heuristics need plausible constants.
    dispatch.cameraNear = kCameraNear;
    dispatch.cameraFar = kCameraFar;
    dispatch.cameraFovAngleVertical = kCameraFovAngleVertical;
    dispatch.viewSpaceToMetersFactor = kViewSpaceToMetersFactor;
    dispatch.flags = 0;

    const ffxReturnCode_t drc =
        catra::ffx::FfxRuntime::Functions().Dispatch(&d.upscaleCtx, &dispatch.header);
    if (drc != FFX_API_RETURN_OK)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: ffxDispatch failed (ret=%u)",
                   static_cast<unsigned>(drc));
        // The list may hold partial FFX recording: do NOT execute it. The next
        // frame discards everything via allocator->Reset + cmdList->Reset.
        return CATRA_ERR_DEVICE;
    }

    hr = d.cmdList->Close();
    if (FAILED(hr))
    {
        return CATRA_ERR_DEVICE;
    }

    ID3D12CommandList* lists[] = {d.cmdList.Get()};
    d.queue->ExecuteCommandLists(1, lists);

    // --- GPU sync with a BOUNDED wait (no INFINITE: a wedged queue must fail)
    ++d.fenceValue;
    hr = d.queue->Signal(d.fence.Get(), d.fenceValue);
    if (SUCCEEDED(hr) && d.fence->GetCompletedValue() < d.fenceValue)
    {
        hr = d.fence->SetEventOnCompletion(d.fenceValue, d.fenceEvent);
        if (SUCCEEDED(hr))
        {
            const DWORD waitResult = WaitForSingleObject(d.fenceEvent, kFenceWaitTimeoutMs);
            if (waitResult != WAIT_OBJECT_0)
            {
                BackendLog(CATRA_LOG_ERROR,
                           "upscale_fsr4: fence wait failed (result=%lu, timeout=%lu ms)",
                           static_cast<unsigned long>(waitResult),
                           static_cast<unsigned long>(kFenceWaitTimeoutMs));
                return CATRA_ERR_DEVICE;
            }
        }
    }
    if (FAILED(hr))
    {
        return CATRA_ERR_DEVICE;
    }

    *outDst = dstRes.Detach(); // caller owns the reference
    return CATRA_OK;
}

int Fsr4Upscaler::RequestSceneCut()
{
    if (m_impl == nullptr)
    {
        return CATRA_ERR_DEVICE; // defensive: Create always populates m_impl
    }
    // No GPU work: the flag is consumed by the next dispatch as reset=true.
    m_impl->pendingReset = true;
    return CATRA_OK;
}

} // namespace catra

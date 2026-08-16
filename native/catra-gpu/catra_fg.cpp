// catra_fg.cpp — FSR 3 Frame Generation playback renderer (SPRINT_04 subtask
// 04). See catra_fg.h for the ABI contract.
//
// ARCHITECTURE (mirrors the official reference
// lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp):
//   * FFX FG swapchain (ForHwnd) created DIRECTLY on the caller's HWND with
//     the bridge's interop DIRECT queue as gameQueue (version descriptor
//     chained via pNext; MakeWindowAssociation after create — no Alt+Enter
//     hijack on the WPF host). Reference: EnableFrameInterpolationSwapchain,
//     fsrapirendermodule.cpp ~1780-1860.
//   * FG context created with ffxCreateBackendDX12Desc (device = the interop
//     D3D12 device) + version descriptor; Configured with the sample's
//     dispatch-forwarding frameGenerationCallback (fsrapirendermodule.cpp
//     ~1204-1219) and frameGenerationEnabled=true.
//   * NO Prepare dispatch: video carries no depth/motion vectors, so the
//     runtime falls back to its INTERNAL optical flow. frameID increments by
//     exactly +1 per presented frame (ffx_framegeneration.h contract).
//   * Frame path: interop share D3D11->D3D12 (ST-15; keyed-mutex consumer
//     half + pool-generation resync, pattern of Fsr4Upscaler::Process) ->
//     letterbox (PS pass) or fast-path copy into the current FG backbuffer ->
//     Present(1,0). The only blocking points are the bounded fence wait
//     (command-object recycling), the keyed-mutex acquire (5 s) and the
//     Present sync itself — cadence belongs to the caller (Story 05).
//   * Teardown (destroy/resize) follows the sample: Configure(enabled=false)
//     — internally waitForPresents(), avoids debug-layer #921
//     OBJECT_DELETED_WHILE_STILL_IN_USE — then DestroyContext(FG),
//     DestroyContext(swapchain), then release the proxy (refcount must hit 0,
//     RestoreApplicationSwapChain ~1784-1791).
//
// EXCEPTION SAFETY: no exception escapes this TU. The FFX callbacks run on
// runtime threads and carry their own try/catch (they must never throw into
// the FFX runtime); entry points are additionally wrapped by GuardCabi in
// catra_gpu.cpp.

#ifndef CATRA_GPU_BUILDING
#define CATRA_GPU_BUILDING // export the CATRA_API symbols from this TU
#endif

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX // std::min below; windows.h min/max macros would break it
#endif

#include "catra_gpu.h"   // pulls in catra_fg.h + CATRA_ERR_* codes
#include "d3d_interop.h" // interop_share_d3d11_to_d3d12 + keyed-mutex helpers
#include "ffx_runtime.h" // FfxRuntime (defines _WINDOWS before the FFX headers)

#include <ffx_framegeneration.h>
#include <dx12/ffx_api_framegeneration_dx12.h>
#include <dx12/ffx_api_dx12.h> // ffxCreateBackendDX12Desc + ffxApiGetResourceDX12

#include <d3d11.h>
#include <d3d12.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstring>
#include <mutex>

// BackendLog lives in catra_gpu.cpp; redeclare the shim so this TU can emit
// through the managed log sink without duplicating the plumbing.
namespace catra {
void BackendLog(int level, const char* fmt, ...);
}

namespace catra {

namespace {

using Microsoft::WRL::ComPtr;

// Bounded fence wait (command-object recycling). Never INFINITE: a wedged
// queue must surface CATRA_ERR_DEVICE, not hang the playback thread.
constexpr DWORD kFenceWaitTimeoutMs = 10000;

// Constant-buffer capacity: CBVs require the GPU virtual address to be
// 256-byte aligned (D3D12_CONSTANT_BUFFER_DATA_PLACEMENT_ALIGNMENT).
constexpr UINT kCbBytes = 256;

// ---------------------------------------------------------------------------
// Letterbox HLSL (vs_5_0 + ps_5_0, compiled via D3DCompile at Create time —
// same pattern as nv12_to_bgra_shader.cpp / the EASU of upscale_fsr1.cpp).
// Fullscreen triangle via SV_VertexId; PS samples the shared frame with a
// bilinear clamp sampler into the aspect-preserved rect and emits black bars
// outside. CB (b0): DstRect = (padX, padY, dstW, dstH), DstSize, SrcSize.
// ---------------------------------------------------------------------------
const char* const kLetterboxHlsl = R"HLSL(
Texture2D<float4> InputTex : register(t0);
SamplerState SamplerLinearClamp : register(s0); // root-sig static sampler

cbuffer LetterboxConst : register(b0)
{
    float4 DstRect; // padX, padY, dstW, dstH (video rect in backbuffer)
    float2 DstSize; // backbuffer width, height
    float2 SrcSize; // source frame width, height
};

struct VSOut
{
    float4 pos : SV_Position;
};

VSOut VSMain(uint vid : SV_VertexId)
{
    VSOut o;
    // Fullscreen triangle: (0,0) (2,0) (0,2) in clip space.
    float2 p = float2((vid << 1) & 2, vid & 2);
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

float4 PSMain(VSOut i) : SV_Target0
{
    // Integer pixel index (SV_Position carries pixel centres: index + 0.5).
    float2 pixel = floor(i.pos.xy);
    float2 local = pixel - DstRect.xy;
    if (local.x < 0.0 || local.y < 0.0 ||
        local.x >= DstRect.z || local.y >= DstRect.w)
    {
        return float4(0.0, 0.0, 0.0, 1.0); // letterbox bar
    }
    // Sample at the mapped pixel centre of the source frame.
    float2 uv = (local + 0.5) / DstRect.zw;
    return float4(InputTex.SampleLevel(SamplerLinearClamp, uv, 0.0).rgb, 1.0);
}
)HLSL";

// RAII guard for the CONSUMER half of the keyed-mutex ping-pong (ST-15) —
// same protocol as upscale_fsr4.cpp: Acquire(k) before reading, Release(k)
// after on EVERY exit path, key alternates 0/1, pool-rebuild resync via
// interop_pool_generation(). The guard also owns the QI reference to the
// mutex (released last, after the sync release).
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
        if (mutex != nullptr)
        {
            mutex->Release(); // drop the QI reference
        }
    }
};

// RAII CloseHandle for the NT shared handle minted by
// interop_share_d3d11_to_d3d12 (header contract: the caller must close it on
// EVERY exit path; ~1 leaked handle per frame exhausts the process table).
struct NtHandleGuard
{
    HANDLE handle = nullptr;
    ~NtHandleGuard()
    {
        if (handle != nullptr)
        {
            CloseHandle(handle);
        }
    }
};

// FFX frame-generation callback: forwards the dispatch to the FG context —
// the EXACT reference-sample pattern (fsrapirendermodule.cpp 1204-1219).
// Runs on FFX runtime threads: no bridge locks, no exceptions.
ffxReturnCode_t FgDispatchTrampoline(ffxDispatchDescFrameGeneration* params,
                                     void* userCtx)
{
    try
    {
        if (params == nullptr || userCtx == nullptr)
        {
            return FFX_API_RETURN_ERROR_PARAMETER;
        }
        if (!catra::ffx::FfxRuntime::IsLoaded())
        {
            return FFX_API_RETURN_ERROR;
        }
        return catra::ffx::FfxRuntime::Functions().Dispatch(
            static_cast<ffxContext*>(userCtx), &params->header);
    }
    catch (...)
    {
        return FFX_API_RETURN_ERROR;
    }
}

} // namespace

// ---------------------------------------------------------------------------
// FgRenderer::Impl
// ---------------------------------------------------------------------------

struct FgRenderer::Impl
{
    // --- borrowed from the bridge (catra_gpu.cpp) --------------------------
    // The interop D3D12 device + DIRECT queue. Shutdown ordering destroys the
    // FG registry BEFORE interop_shutdown, so both outlive the renderer.
    ID3D12Device* device12 = nullptr;
    ID3D12CommandQueue* queue12 = nullptr; // gameQueue of the FG swapchain

    HWND hwnd = nullptr;
    int w = 0;
    int h = 0;
    double videoFps = 0.0;

    // --- FFX state ----------------------------------------------------------
    ffxContext swapchainCtx = nullptr;
    ffxContext fgCtx = nullptr;
    ComPtr<IDXGISwapChain4> fgSwapchain; // the FG proxy swapchain
    ComPtr<IDXGIFactory> factory;        // create-time factory (desc lifetime)
    // The swapchain desc must stay live until ffxDestroyContext of the
    // swapchain context (ffx_api.h: "pointers passed in desc must remain live
    // until ffxDestroyContext") — kept on the Impl, never a stack local.
    DXGI_SWAP_CHAIN_DESC1 desc1 = {};

    // frameID: exactly +1 per presented frame; continues across resize.
    uint64_t frameID = 0;
    bool dead = false; // device fault / failed recreate: present/resize fail,
                       // destroy stays safe

    // --- render-pass plumbing (size-independent, created once) --------------
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> cmdList;
    ComPtr<ID3D12Fence> fence;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;

    ComPtr<ID3D12RootSignature> rootSig;
    ComPtr<ID3D12PipelineState> pso;
    ComPtr<ID3D12DescriptorHeap> srvHeap;   // shader-visible, 1 SRV slot
    ComPtr<ID3D12Resource> cbUpload;        // 256 B upload, persistently mapped
    void* cbMapped = nullptr;

    // --- swapchain-dependent objects (rebuilt on resize) --------------------
    ComPtr<ID3D12DescriptorHeap> rtvHeap;   // CPU-only, 2 RTVs
    ComPtr<ID3D12Resource> backbuffers[2];

    // --- keyed-mutex consumer half (ST-15 ping-pong) ------------------------
    uint64_t consumerKey = 0;
    uint64_t lastPoolGeneration = 0;

    // --- present observer (test hook; FFX threads) ---------------------------
    std::mutex observerMutex;
    FgPresentObserverFn observerCb = nullptr;
    void* observerUser = nullptr;
};

namespace {

// Waits (bounded) for all work signalled so far on our fence — i.e. the last
// recorded render pass is GPU-complete. First call (fenceValue == 0) is a
// no-op. Failure marks nothing; the caller decides (teardown tolerates it).
int WaitForPreviousFence(FgRenderer::Impl& d)
{
    if (d.fence == nullptr || d.fenceValue == 0)
    {
        return CATRA_OK;
    }
    if (d.fence->GetCompletedValue() >= d.fenceValue)
    {
        return CATRA_OK;
    }
    HRESULT hr = d.fence->SetEventOnCompletion(d.fenceValue, d.fenceEvent);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: SetEventOnCompletion hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    const DWORD waitResult = WaitForSingleObject(d.fenceEvent, kFenceWaitTimeoutMs);
    if (waitResult != WAIT_OBJECT_0)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: fence wait failed (result=%lu, timeout=%lu ms)",
                   static_cast<unsigned long>(waitResult),
                   static_cast<unsigned long>(kFenceWaitTimeoutMs));
        return CATRA_ERR_DEVICE;
    }
    return CATRA_OK;
}

// FFX present callback: forwards to the installed observer (smoke-test
// counting point — rendered AND generated frames). Runs on FFX pacing
// threads: tiny lock only to snapshot the pointer pair, never a bridge lock.
ffxReturnCode_t PresentTrampoline(ffxCallbackDescFrameGenerationPresent* params,
                                  void* userCtx)
{
    try
    {
        FgRenderer::Impl* d = static_cast<FgRenderer::Impl*>(userCtx);
        if (d != nullptr && params != nullptr)
        {
            FgPresentObserverFn cb = nullptr;
            void* user = nullptr;
            {
                std::lock_guard<std::mutex> lock(d->observerMutex);
                cb = d->observerCb;
                user = d->observerUser;
            }
            if (cb != nullptr)
            {
                cb(params->isGeneratedFrame, user);
            }
        }
        return FFX_API_RETURN_OK;
    }
    catch (...)
    {
        return FFX_API_RETURN_ERROR;
    }
}

} // namespace

// ---------------------------------------------------------------------------
// Swapchain / FG-context lifecycle helpers (member-style free functions; the
// Impl is passed explicitly so they work from Create, Resize and teardown).
// ---------------------------------------------------------------------------

namespace {

// Builds (or rebuilds) the FG swapchain + FG context + backbuffer RTVs for
// size d.w x d.h. The render pipeline/plumbing must already exist. Returns
// CATRA_OK or CATRA_ERR_DEVICE with partial state left for the caller's
// teardown pass (null-safe).
int BuildSwapchainAndFg(FgRenderer::Impl& d)
{
    const auto& ffx = catra::ffx::FfxRuntime::Functions();

    // --- FG swapchain (ForHwnd) + chained version descriptor ----------------
    // Reference: fsrapirendermodule.cpp EnableFrameInterpolationSwapchain
    // (~1806-1850). fullscreenDesc = nullptr -> windowed.
    d.desc1 = {};
    d.desc1.Width = static_cast<UINT>(d.w);
    d.desc1.Height = static_cast<UINT>(d.h);
    d.desc1.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.desc1.Stereo = FALSE;
    d.desc1.SampleDesc.Count = 1;
    d.desc1.SampleDesc.Quality = 0;
    d.desc1.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d.desc1.BufferCount = 2;
    d.desc1.Scaling = DXGI_SCALING_NONE;
    d.desc1.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d.desc1.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    d.desc1.Flags = 0;

    IDXGISwapChain4* swapOut = nullptr;
    ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 sc = {};
    sc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12;
    sc.swapchain = &swapOut;
    sc.hwnd = d.hwnd;
    sc.desc = &d.desc1;          // Impl member: lives until destroy
    sc.fullscreenDesc = nullptr; // windowed
    sc.dxgiFactory = d.factory.Get();
    sc.gameQueue = d.queue12;    // the interop DIRECT queue

    ffxCreateContextDescFrameGenerationSwapChainVersionDX12 scVer = {};
    scVer.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_VERSION_DX12;
    scVer.version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION;
    sc.header.pNext = &scVer.header;

    ffxReturnCode_t rc = ffx.CreateContext(&d.swapchainCtx, &sc.header, nullptr);
    if (rc != FFX_API_RETURN_OK || swapOut == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: ffxCreateContext(FG swapchain ForHwnd %dx%d) "
                   "failed ret=%u", d.w, d.h, static_cast<unsigned>(rc));
        if (swapOut != nullptr)
        {
            swapOut->Release();
        }
        d.swapchainCtx = nullptr;
        return CATRA_ERR_DEVICE;
    }
    d.fgSwapchain.Attach(swapOut);

    // Alt+Enter must not hijack the WPF host window (sample does the same
    // after creating a different swapchain on the HWND).
    IDXGIFactory* parentFactory = nullptr;
    if (SUCCEEDED(d.fgSwapchain->GetParent(IID_PPV_ARGS(&parentFactory))))
    {
        parentFactory->MakeWindowAssociation(d.hwnd, DXGI_MWA_NO_WINDOW_CHANGES);
        parentFactory->Release();
    }

    // --- FG context: createFg -> backendDesc -> headerVersion (sample order,
    //     fsrapirendermodule.cpp ~1100-1226). Video = SDR, no async. --------
    ffxCreateContextDescFrameGeneration fg = {};
    fg.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    fg.flags = 0;
    fg.displaySize.width = static_cast<uint32_t>(d.w);
    fg.displaySize.height = static_cast<uint32_t>(d.h);
    fg.maxRenderSize.width = static_cast<uint32_t>(d.w);
    fg.maxRenderSize.height = static_cast<uint32_t>(d.h);
    fg.backBufferFormat = FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM;

    ffxCreateBackendDX12Desc backend = {};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = d.device12;

    ffxCreateContextDescFrameGenerationVersion fgVer = {};
    fgVer.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    fgVer.version = FFX_FRAMEGENERATION_VERSION;

    fg.header.pNext = &backend.header;
    backend.header.pNext = &fgVer.header;

    rc = ffx.CreateContext(&d.fgCtx, &fg.header, nullptr);
    if (rc != FFX_API_RETURN_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: ffxCreateContext(FG context %dx%d) failed ret=%u",
                   d.w, d.h, static_cast<unsigned>(rc));
        return CATRA_ERR_DEVICE;
    }

    // --- Backbuffers + RTVs (flip-discard pair) ------------------------------
    if (d.rtvHeap == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "catra_fg: RTV heap missing before FG build");
        return CATRA_ERR_DEVICE;
    }
    HRESULT hr = S_OK;
    for (int i = 0; i < 2 && SUCCEEDED(hr); ++i)
    {
        hr = d.fgSwapchain->GetBuffer(static_cast<UINT>(i),
                                      IID_PPV_ARGS(d.backbuffers[i].GetAddressOf()));
    }
    if (SUCCEEDED(hr))
    {
        const UINT inc = d.device12->GetDescriptorHandleIncrementSize(
            D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12_CPU_DESCRIPTOR_HANDLE rtv0 = d.rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < 2; ++i)
        {
            D3D12_RENDER_TARGET_VIEW_DESC rtvDesc = {};
            rtvDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            rtvDesc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
            d.device12->CreateRenderTargetView(d.backbuffers[i].Get(), &rtvDesc, rtv0);
            rtv0.ptr += inc;
        }
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: backbuffer/RTV setup hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Configure (reference fsrapirendermodule.cpp ~1198-1224) ------------
    ffxConfigureDescFrameGeneration cfg = {};
    cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    cfg.swapChain = d.fgSwapchain.Get();
    cfg.frameGenerationCallback = &FgDispatchTrampoline;
    cfg.frameGenerationCallbackUserContext = &d.fgCtx; // sample pattern
    cfg.presentCallback = &PresentTrampoline; // counting point; no observer = near-free
    cfg.presentCallbackUserContext = &d;
    cfg.frameGenerationEnabled = true;
    cfg.allowAsyncWorkloads = false;
    cfg.HUDLessColor = FfxApiResource{}; // video: no HUD layer
    cfg.flags = 0;
    cfg.onlyPresentGenerated = false;
    cfg.generationRect.left = 0;   // full area: the letterbox bars are static
    cfg.generationRect.top = 0;    // black — interpolating the whole frame is
    cfg.generationRect.width = d.w;   // correct and avoids a re-configure per
    cfg.generationRect.height = d.h;  // aspect change
    cfg.frameID = d.frameID;       // continues across resize — no gap, no reset

    rc = ffx.Configure(&d.fgCtx, &cfg.header);
    if (rc != FFX_API_RETURN_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: ffxConfigure(FG enabled) failed ret=%u",
                   static_cast<unsigned>(rc));
        return CATRA_ERR_DEVICE;
    }

    BackendLog(CATRA_LOG_INFO,
               "catra_fg: FG swapchain + context ready %dx%d @ video %.3f fps "
               "(fg ver=%u, swapchain ver=%u, frameID=%llu)",
               d.w, d.h, d.videoFps,
               static_cast<unsigned>(FFX_FRAMEGENERATION_VERSION),
               static_cast<unsigned>(FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION),
               static_cast<unsigned long long>(d.frameID));
    return CATRA_OK;
}

// Sample-ordered FFX teardown (fsrapirendermodule.cpp ~1232-1254 +
// RestoreApplicationSwapChain ~1765-1804). Null-safe on every member; safe
// when the loader was already unloaded or the context is dead.
void TearDownFfxObjects(FgRenderer::Impl& d)
{
    // Drain our last render pass before anyone releases its targets.
    WaitForPreviousFence(d);

    // Drop OUR backbuffer references FIRST: once the proxy swapchain is
    // destroyed its underlying DXGI backbuffers are invalidated — releasing
    // them afterwards crashes on some drivers. The FFX runtime holds its own
    // references for the flush below.
    for (int i = 0; i < 2; ++i)
    {
        d.backbuffers[i].Reset();
    }

    const bool ffxLive = catra::ffx::FfxRuntime::IsLoaded();
    const auto& ffx = catra::ffx::FfxRuntime::Functions();

    // (b) Disable FG before destroy: internally waitForPresents(), flushing
    // interpolation/UI GPU work — avoids D3D12 debug-layer error #921
    // OBJECT_DELETED_WHILE_STILL_IN_USE (comment at sample lines 1237-1240).
    if (d.fgCtx != nullptr && ffxLive && !d.dead)
    {
        ffxConfigureDescFrameGeneration cfg = {};
        cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        cfg.swapChain = d.fgSwapchain.Get();
        cfg.frameGenerationCallback = &FgDispatchTrampoline;
        cfg.frameGenerationCallbackUserContext = &d.fgCtx;
        cfg.presentCallback = nullptr;
        cfg.presentCallbackUserContext = nullptr;
        cfg.frameGenerationEnabled = false;
        cfg.allowAsyncWorkloads = false;
        cfg.HUDLessColor = FfxApiResource{};
        cfg.flags = 0;
        cfg.onlyPresentGenerated = false;
        cfg.generationRect.left = 0;
        cfg.generationRect.top = 0;
        cfg.generationRect.width = d.w;
        cfg.generationRect.height = d.h;
        cfg.frameID = d.frameID;
        const ffxReturnCode_t crc = ffx.Configure(&d.fgCtx, &cfg.header);
        if (crc != FFX_API_RETURN_OK)
        {
            BackendLog(CATRA_LOG_WARN,
                       "catra_fg: Configure(disable FG) ret=%u (teardown continues)",
                       static_cast<unsigned>(crc));
        }
    }

    // (c)+(d) destroy FG context, then the swapchain context.
    if (d.fgCtx != nullptr)
    {
        if (ffxLive)
        {
            ffx.DestroyContext(&d.fgCtx, nullptr);
        }
        d.fgCtx = nullptr;
    }
    if (d.swapchainCtx != nullptr)
    {
        if (ffxLive)
        {
            ffx.DestroyContext(&d.swapchainCtx, nullptr);
        }
        d.swapchainCtx = nullptr;
    }

    // (e) release the proxy: refcount must hit zero, otherwise something kept
    // it alive -> zombie/black window on the HWND (sample asserts this).
    if (d.fgSwapchain != nullptr)
    {
        // Detach FIRST: a manual ->Release() while the ComPtr still owns the
        // pointer would double-release (the ComPtr's Reset/assignment releases
        // again -> use-after-free crash, observed on resize teardown).
        IDXGISwapChain4* raw = d.fgSwapchain.Detach();
        const ULONG ref = raw->Release();
        if (ref != 0)
        {
            BackendLog(CATRA_LOG_WARN,
                       "catra_fg: proxy swapchain refcount=%lu after release "
                       "(expected 0)", static_cast<unsigned long>(ref));
        }
    }

    // (f) the backbuffer refs were dropped FIRST (see the top of this
    // function); the RTV HEAP is kept (size-independent, reused by the rebuild
    // pass — its descriptors are re-created against the fresh backbuffers).
}

} // namespace

// ---------------------------------------------------------------------------
// FgRenderer
// ---------------------------------------------------------------------------

int FgRenderer::Create(void* device12v, void* queue12v, void* hwndv,
                       int w, int h, double video_fps, FgRenderer** out)
{
    if (out != nullptr)
    {
        *out = nullptr;
    }
    if (device12v == nullptr || queue12v == nullptr || out == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "catra_fg: Create null device/queue/out");
        return CATRA_ERR_INVALID_ARG;
    }
    if (hwndv == nullptr || !IsWindow(static_cast<HWND>(hwndv)) ||
        w <= 0 || h <= 0 || video_fps <= 0.0)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: Create invalid args (hwnd=%p valid=%d %dx%d fps=%.3f)",
                   hwndv, hwndv != nullptr ? IsWindow(static_cast<HWND>(hwndv)) : 0,
                   w, h, video_fps);
        return CATRA_ERR_INVALID_ARG;
    }

    // Runtime gate BEFORE touching DX12 state: missing loader/DLLs/adapter is
    // NOT_IMPL (the automatic fallback of Story 05 keys off this), a missing
    // interop device (soft-fail of catra_init) is DEVICE.
    if (!catra::ffx::FfxRuntime::IsAvailable())
    {
        BackendLog(CATRA_LOG_WARN,
                   "catra_fg: FFX runtime unavailable -> CATRA_ERR_NOT_IMPL");
        return CATRA_ERR_NOT_IMPL;
    }

    auto self = std::unique_ptr<FgRenderer>(new FgRenderer());
    self->m_impl = new Impl();
    Impl& d = *self->m_impl;
    d.device12 = static_cast<ID3D12Device*>(device12v);
    d.queue12 = static_cast<ID3D12CommandQueue*>(queue12v);
    d.hwnd = static_cast<HWND>(hwndv);
    d.w = w;
    d.h = h;
    d.videoFps = video_fps;

    // --- DXGI factory (create-time; kept for desc lifetime) ----------------
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(d.factory.GetAddressOf()));
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "catra_fg: CreateDXGIFactory1 hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- size-independent render plumbing FIRST (built once; the FG build
    //     below needs the RTV heap already alive for the backbuffer RTVs) ----
    int rc = CATRA_OK;
    hr = d.device12->CreateCommandAllocator(
        D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(d.allocator.GetAddressOf()));
    if (SUCCEEDED(hr))
    {
        hr = d.device12->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, d.allocator.Get(), nullptr,
            IID_PPV_ARGS(d.cmdList.GetAddressOf()));
    }
    if (SUCCEEDED(hr))
    {
        d.cmdList->Close();
        hr = d.device12->CreateFence(0, D3D12_FENCE_FLAG_NONE,
                                     IID_PPV_ARGS(d.fence.GetAddressOf()));
    }
    if (SUCCEEDED(hr))
    {
        d.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (d.fenceEvent == nullptr)
        {
            hr = E_FAIL;
        }
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: command plumbing hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        rc = CATRA_ERR_DEVICE;
    }

    // --- letterbox pipeline: shaders, root signature, PSO -------------------
    if (rc == CATRA_OK)
    {
        ComPtr<ID3DBlob> vsBlob;
        ComPtr<ID3DBlob> psBlob;
        ComPtr<ID3DBlob> errBlob;
        hr = D3DCompile(kLetterboxHlsl, std::strlen(kLetterboxHlsl),
                        "catra_fg_letterbox", nullptr, nullptr, "VSMain",
                        "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                        vsBlob.GetAddressOf(), errBlob.GetAddressOf());
        if (SUCCEEDED(hr))
        {
            hr = D3DCompile(kLetterboxHlsl, std::strlen(kLetterboxHlsl),
                            "catra_fg_letterbox", nullptr, nullptr, "PSMain",
                            "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                            psBlob.GetAddressOf(), errBlob.GetAddressOf());
        }
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR, "catra_fg: letterbox D3DCompile hr=0x%08lX%s%s",
                       static_cast<unsigned long>(hr),
                       errBlob ? ": " : "",
                       errBlob ? static_cast<const char*>(errBlob->GetBufferPointer()) : "");
            rc = CATRA_ERR_DEVICE;
        }

        if (rc == CATRA_OK)
        {
            // Root signature v1.0: root CBV (b0) + 1-SRV descriptor table
            // (t0) + static linear-clamp sampler (s0). No IA input layout.
            D3D12_DESCRIPTOR_RANGE srvRange = {};
            srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
            srvRange.NumDescriptors = 1;
            srvRange.BaseShaderRegister = 0;
            srvRange.RegisterSpace = 0;
            srvRange.OffsetInDescriptorsFromTableStart =
                D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

            D3D12_ROOT_PARAMETER params[2] = {};
            params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
            params[0].Descriptor.ShaderRegister = 0;
            params[0].Descriptor.RegisterSpace = 0;
            params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
            params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            params[1].DescriptorTable.NumDescriptorRanges = 1;
            params[1].DescriptorTable.pDescriptorRanges = &srvRange;
            params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

            D3D12_STATIC_SAMPLER_DESC sampler = {};
            sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
            sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            sampler.MipLODBias = 0.0f;
            sampler.MaxAnisotropy = 1;
            sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
            sampler.BorderColor = D3D12_STATIC_BORDER_COLOR_TRANSPARENT_BLACK;
            sampler.MinLOD = 0.0f;
            sampler.MaxLOD = D3D12_FLOAT32_MAX;
            sampler.ShaderRegister = 0;
            sampler.RegisterSpace = 0;
            sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;

            D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
            rsDesc.NumParameters = 2;
            rsDesc.pParameters = params;
            rsDesc.NumStaticSamplers = 1;
            rsDesc.pStaticSamplers = &sampler;
            rsDesc.Flags =
                D3D12_ROOT_SIGNATURE_FLAG_DENY_VERTEX_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_HULL_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_DOMAIN_SHADER_ROOT_ACCESS |
                D3D12_ROOT_SIGNATURE_FLAG_DENY_GEOMETRY_SHADER_ROOT_ACCESS;
            // NOTE: no ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT flag — the fullscreen
            // triangle is generated in the VS (SV_VertexId), no IA stage.

            ComPtr<ID3DBlob> rsBlob;
            ComPtr<ID3DBlob> rsErr;
            hr = D3D12SerializeRootSignature(&rsDesc,
                                             D3D_ROOT_SIGNATURE_VERSION_1_0,
                                             rsBlob.GetAddressOf(),
                                             rsErr.GetAddressOf());
            if (SUCCEEDED(hr))
            {
                hr = d.device12->CreateRootSignature(
                    0, rsBlob->GetBufferPointer(), rsBlob->GetBufferSize(),
                    IID_PPV_ARGS(d.rootSig.GetAddressOf()));
            }
            if (FAILED(hr))
            {
                BackendLog(CATRA_LOG_ERROR,
                           "catra_fg: root signature hr=0x%08lX",
                           static_cast<unsigned long>(hr));
                rc = CATRA_ERR_DEVICE;
            }
        }

        if (rc == CATRA_OK)
        {
            D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc = {};
            psoDesc.pRootSignature = d.rootSig.Get();
            psoDesc.VS.pShaderBytecode = vsBlob->GetBufferPointer();
            psoDesc.VS.BytecodeLength = vsBlob->GetBufferSize();
            psoDesc.PS.pShaderBytecode = psBlob->GetBufferPointer();
            psoDesc.PS.BytecodeLength = psBlob->GetBufferSize();
            psoDesc.BlendState.AlphaToCoverageEnable = FALSE;
            psoDesc.BlendState.IndependentBlendEnable = FALSE;
            psoDesc.BlendState.RenderTarget[0].BlendEnable = FALSE;
            psoDesc.BlendState.RenderTarget[0].LogicOpEnable = FALSE;
            psoDesc.BlendState.RenderTarget[0].RenderTargetWriteMask =
                D3D12_COLOR_WRITE_ENABLE_ALL;
            psoDesc.SampleMask = UINT_MAX;
            psoDesc.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
            psoDesc.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
            psoDesc.RasterizerState.FrontCounterClockwise = FALSE;
            psoDesc.RasterizerState.DepthBias = D3D12_DEFAULT_DEPTH_BIAS;
            psoDesc.RasterizerState.DepthBiasClamp = D3D12_DEFAULT_DEPTH_BIAS_CLAMP;
            psoDesc.RasterizerState.SlopeScaledDepthBias =
                D3D12_DEFAULT_SLOPE_SCALED_DEPTH_BIAS;
            psoDesc.RasterizerState.DepthClipEnable = FALSE;
            psoDesc.RasterizerState.MultisampleEnable = FALSE;
            psoDesc.RasterizerState.AntialiasedLineEnable = FALSE;
            psoDesc.RasterizerState.ForcedSampleCount = 0;
            psoDesc.RasterizerState.ConservativeRaster =
                D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;
            psoDesc.DepthStencilState.DepthEnable = FALSE;
            psoDesc.DepthStencilState.StencilEnable = FALSE;
            psoDesc.InputLayout.pInputElementDescs = nullptr;
            psoDesc.InputLayout.NumElements = 0;
            psoDesc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            psoDesc.NumRenderTargets = 1;
            psoDesc.RTVFormats[0] = DXGI_FORMAT_B8G8R8A8_UNORM;
            psoDesc.SampleDesc.Count = 1;
            psoDesc.SampleDesc.Quality = 0;

            hr = d.device12->CreateGraphicsPipelineState(
                &psoDesc, IID_PPV_ARGS(d.pso.GetAddressOf()));
            if (FAILED(hr))
            {
                BackendLog(CATRA_LOG_ERROR,
                           "catra_fg: PSO hr=0x%08lX",
                           static_cast<unsigned long>(hr));
                rc = CATRA_ERR_DEVICE;
            }
        }
    }

    // --- heaps + constant buffer (the RTV heap must exist BEFORE the FG
    //     build creates the backbuffer RTVs) ---------------------------------
    if (rc == CATRA_OK)
    {
        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
        srvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvHeapDesc.NumDescriptors = 1;
        srvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        HRESULT hrHeap = d.device12->CreateDescriptorHeap(
            &srvHeapDesc, IID_PPV_ARGS(d.srvHeap.GetAddressOf()));

        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rtvHeapDesc.NumDescriptors = 2;
        rtvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE; // CPU-only
        if (SUCCEEDED(hrHeap))
        {
            hrHeap = d.device12->CreateDescriptorHeap(
                &rtvHeapDesc, IID_PPV_ARGS(d.rtvHeap.GetAddressOf()));
        }

        D3D12_HEAP_PROPERTIES uploadHeap = {};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC cbDesc = {};
        cbDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        cbDesc.Width = kCbBytes;
        cbDesc.Height = 1;
        cbDesc.DepthOrArraySize = 1;
        cbDesc.MipLevels = 1;
        cbDesc.Format = DXGI_FORMAT_UNKNOWN;
        cbDesc.SampleDesc.Count = 1;
        cbDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        if (SUCCEEDED(hrHeap))
        {
            hrHeap = d.device12->CreateCommittedResource(
                &uploadHeap, D3D12_HEAP_FLAG_NONE, &cbDesc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                IID_PPV_ARGS(d.cbUpload.GetAddressOf()));
        }
        if (SUCCEEDED(hrHeap))
        {
            hrHeap = d.cbUpload->Map(0, nullptr, &d.cbMapped);
        }
        if (FAILED(hrHeap))
        {
            BackendLog(CATRA_LOG_ERROR,
                       "catra_fg: heaps/CB hr=0x%08lX",
                       static_cast<unsigned long>(hrHeap));
            rc = CATRA_ERR_DEVICE;
        }
    }

    // --- FG swapchain + FG context + RTVs + Configure (runs LAST: every
    //     heap/pipeline object it depends on is alive by now) ----------------
    if (rc == CATRA_OK)
    {
        rc = BuildSwapchainAndFg(d);
    }

    if (rc != CATRA_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: create failed rc=%d — tearing down partial state", rc);
        TearDownFfxObjects(d); // null-safe
        return rc; // ~FgRenderer releases the rest (RAII)
    }

    BackendLog(CATRA_LOG_INFO,
               "catra_fg: renderer created %dx%d (video %.3f fps, hwnd=%p)",
               w, h, video_fps, hwndv);
    *out = self.release();
    return CATRA_OK;
}

int FgRenderer::Present(void* d3d11_texture, int frame_w, int frame_h)
{
    if (m_impl == nullptr)
    {
        return CATRA_ERR_DEVICE; // destroyed
    }
    Impl& d = *m_impl;
    if (d.dead)
    {
        return CATRA_ERR_DEVICE;
    }
    if (d3d11_texture == nullptr || frame_w <= 0 || frame_h <= 0)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    if (!catra::ffx::FfxRuntime::IsLoaded())
    {
        return CATRA_ERR_DEVICE;
    }

    // --- 1. share D3D11 -> D3D12 (ST-15 interop) ----------------------------
    // Pooled GPU-GPU copy (or zero-copy NT share). The NT handle (when minted)
    // is closed on EVERY exit path; the D3D12 reference is ComPtr-owned.
    ComPtr<ID3D12Resource> tex12;
    NtHandleGuard ntGuard;
    const int src = catra::interop_share_d3d11_to_d3d12(
        static_cast<ID3D11Texture2D*>(d3d11_texture),
        tex12.GetAddressOf(), &ntGuard.handle);
    if (src != CATRA_OK || tex12 == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "catra_fg: interop share rc=%d", src);
        return src != CATRA_OK ? src : CATRA_ERR_DEVICE;
    }

    // --- 2. keyed-mutex consumer half (Fsr4Upscaler::Process pattern) -------
    KeyedMutexGuard mutexGuard;
    mutexGuard.nextKey = &d.consumerKey;
    tex12->QueryInterface(__uuidof(IDXGIKeyedMutex),
                          reinterpret_cast<void**>(&mutexGuard.mutex));
    if (mutexGuard.mutex != nullptr)
    {
        // Pool-rebuild resync: fresh mutexes + producer key reset -> consumer
        // key must return to 0 in lockstep (d3d_interop header banner).
        const uint64_t generation = catra::interop_pool_generation();
        if (generation != d.lastPoolGeneration)
        {
            d.consumerKey = 0;
            d.lastPoolGeneration = generation;
        }
        mutexGuard.key = d.consumerKey;
        const int arc = catra::interop_acquire(mutexGuard.mutex, mutexGuard.key, 5000);
        if (arc != CATRA_OK)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "catra_fg: keyed-mutex acquire rc=%d (key=%llu)",
                       arc, static_cast<unsigned long long>(mutexGuard.key));
            return CATRA_ERR_DEVICE;
        }
        mutexGuard.held = true; // destructor releases on every exit from here
    }

    // --- 3. bounded wait for the previous frame (recycle allocator/list) ----
    if (WaitForPreviousFence(d) != CATRA_OK)
    {
        d.dead = true;
        return CATRA_ERR_DEVICE;
    }

    // --- 4. record: barriers + letterbox/copy into the FG backbuffer --------
    HRESULT hr = d.allocator->Reset();
    if (SUCCEEDED(hr))
    {
        hr = d.cmdList->Reset(d.allocator.Get(), nullptr);
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "catra_fg: list reset hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        d.dead = true;
        return CATRA_ERR_DEVICE;
    }

    const UINT bbIdx = d.fgSwapchain->GetCurrentBackBufferIndex() & 1u;
    ID3D12Resource* bb = d.backbuffers[bbIdx].Get();
    const bool fastPath = (frame_w == d.w && frame_h == d.h);

    D3D12_RESOURCE_BARRIER inBarriers[2] = {};
    inBarriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    inBarriers[0].Transition.pResource = bb;
    inBarriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    inBarriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    inBarriers[0].Transition.StateAfter = fastPath
        ? D3D12_RESOURCE_STATE_COPY_DEST
        : D3D12_RESOURCE_STATE_RENDER_TARGET;
    inBarriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    inBarriers[1].Transition.pResource = tex12.Get();
    inBarriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    inBarriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    inBarriers[1].Transition.StateAfter = fastPath
        ? D3D12_RESOURCE_STATE_COPY_SOURCE
        : D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
    d.cmdList->ResourceBarrier(2, inBarriers);

    if (fastPath)
    {
        // Same geometry: straight copy of subresource 0.
        D3D12_BOX box = {};
        box.left = 0;
        box.top = 0;
        box.front = 0;
        box.right = static_cast<UINT>(frame_w);
        box.bottom = static_cast<UINT>(frame_h);
        box.back = 1;
        D3D12_TEXTURE_COPY_LOCATION dstLoc = {};
        dstLoc.pResource = bb;
        dstLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dstLoc.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
        srcLoc.pResource = tex12.Get();
        srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        srcLoc.SubresourceIndex = 0;
        d.cmdList->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, &box);
    }
    else
    {
        // Letterbox: aspect-preserved rect, centred (any direction scale).
        const float scale = std::min(static_cast<float>(d.w) / static_cast<float>(frame_w),
                                     static_cast<float>(d.h) / static_cast<float>(frame_h));
        const int dstW = std::max(1, static_cast<int>(frame_w * scale));
        const int dstH = std::max(1, static_cast<int>(frame_h * scale));
        const int padX = (d.w - dstW) / 2;
        const int padY = (d.h - dstH) / 2;

        float cb[8];
        cb[0] = static_cast<float>(padX);
        cb[1] = static_cast<float>(padY);
        cb[2] = static_cast<float>(dstW);
        cb[3] = static_cast<float>(dstH);
        cb[4] = static_cast<float>(d.w);
        cb[5] = static_cast<float>(d.h);
        cb[6] = static_cast<float>(frame_w);
        cb[7] = static_cast<float>(frame_h);
        std::memcpy(d.cbMapped, cb, sizeof(cb)); // safe: previous frame fenced

        // Per-frame SRV of the shared texture into the single heap slot (the
        // GPU is idle here — fence waited above).
        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
        srvDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srvDesc.Texture2D.MostDetailedMip = 0;
        srvDesc.Texture2D.MipLevels = 1;
        srvDesc.Texture2D.PlaneSlice = 0;
        srvDesc.Texture2D.ResourceMinLODClamp = 0.0f;
        d.device12->CreateShaderResourceView(tex12.Get(), &srvDesc,
                                             d.srvHeap->GetCPUDescriptorHandleForHeapStart());

        const D3D12_CPU_DESCRIPTOR_HANDLE rtvHandle =
            d.rtvHeap->GetCPUDescriptorHandleForHeapStart();
        const UINT rtvInc = d.device12->GetDescriptorHandleIncrementSize(
            D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        D3D12_CPU_DESCRIPTOR_HANDLE target = rtvHandle;
        target.ptr += rtvInc * bbIdx;

        d.cmdList->OMSetRenderTargets(1, &target, FALSE, nullptr);

        D3D12_VIEWPORT vp = {};
        vp.Width = static_cast<FLOAT>(d.w);
        vp.Height = static_cast<FLOAT>(d.h);
        vp.MaxDepth = 1.0f;
        d.cmdList->RSSetViewports(1, &vp);
        D3D12_RECT scissor = {};
        scissor.right = d.w;
        scissor.bottom = d.h;
        d.cmdList->RSSetScissorRects(1, &scissor);

        d.cmdList->SetPipelineState(d.pso.Get());
        d.cmdList->SetGraphicsRootSignature(d.rootSig.Get());
        ID3D12DescriptorHeap* heaps[] = {d.srvHeap.Get()};
        d.cmdList->SetDescriptorHeaps(1, heaps);
        d.cmdList->SetGraphicsRootConstantBufferView(0, d.cbUpload->GetGPUVirtualAddress());
        d.cmdList->SetGraphicsRootDescriptorTable(
            1, d.srvHeap->GetGPUDescriptorHandleForHeapStart());
        d.cmdList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        d.cmdList->DrawInstanced(3, 1, 0, 0);
    }

    // --- 5. exit barriers: tex12 -> COMMON (release the keyed mutex clean),
    //        backbuffer -> PRESENT -------------------------------------------
    D3D12_RESOURCE_BARRIER outBarriers[2] = {};
    outBarriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    outBarriers[0].Transition.pResource = tex12.Get();
    outBarriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    outBarriers[0].Transition.StateBefore = fastPath
        ? D3D12_RESOURCE_STATE_COPY_SOURCE
        : D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
    outBarriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    outBarriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    outBarriers[1].Transition.pResource = bb;
    outBarriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    outBarriers[1].Transition.StateBefore = fastPath
        ? D3D12_RESOURCE_STATE_COPY_DEST
        : D3D12_RESOURCE_STATE_RENDER_TARGET;
    outBarriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    d.cmdList->ResourceBarrier(2, outBarriers);

    hr = d.cmdList->Close();
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "catra_fg: list close hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        d.dead = true;
        return CATRA_ERR_DEVICE;
    }

    // --- 6. execute + present through the FG proxy ---------------------------
    ID3D12CommandList* lists[] = {d.cmdList.Get()};
    d.queue12->ExecuteCommandLists(1, lists);

    const HRESULT phr = d.fgSwapchain->Present(1, 0);
    if (FAILED(phr))
    {
        const HRESULT removed = d.device12->GetDeviceRemovedReason();
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: Present hr=0x%08lX (device removed reason=0x%08lX) "
                   "-> context dead",
                   static_cast<unsigned long>(phr),
                   static_cast<unsigned long>(removed));
        d.dead = true;
        return CATRA_ERR_DEVICE;
    }

    // --- 7. signal + frameID (exactly +1 per present — ffx contract) --------
    ++d.fenceValue;
    d.queue12->Signal(d.fence.Get(), d.fenceValue);
    d.frameID += 1;
    return CATRA_OK; // mutexGuard + ntGuard release/close here
}

int FgRenderer::Resize(int w, int h)
{
    if (m_impl == nullptr)
    {
        return CATRA_ERR_DEVICE;
    }
    Impl& d = *m_impl;
    if (w <= 0 || h <= 0)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    if (d.dead)
    {
        return CATRA_ERR_DEVICE;
    }
    if (w == d.w && h == d.h)
    {
        return CATRA_OK; // no-op
    }
    if (!catra::ffx::FfxRuntime::IsLoaded())
    {
        d.dead = true;
        return CATRA_ERR_DEVICE;
    }

    const int oldW = d.w;
    const int oldH = d.h;

    // Teardown in sample order (configure-disabled flushes presents) ...
    TearDownFfxObjects(d);

    // ... then recreate at the new size on the SAME HWND. frameID continues.
    d.w = w;
    d.h = h;
    const int rc = BuildSwapchainAndFg(d);
    if (rc != CATRA_OK)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "catra_fg: resize %dx%d -> %dx%d recreate failed rc=%d -> dead",
                   oldW, oldH, w, h, rc);
        TearDownFfxObjects(d); // leave nothing half-alive
        d.dead = true;
        return CATRA_ERR_DEVICE;
    }

    BackendLog(CATRA_LOG_INFO, "catra_fg: resized %dx%d -> %dx%d (frameID=%llu)",
               oldW, oldH, w, h, static_cast<unsigned long long>(d.frameID));
    return CATRA_OK;
}

void FgRenderer::SetPresentObserver(FgPresentObserverFn cb, void* user)
{
    if (m_impl == nullptr)
    {
        return;
    }
    std::lock_guard<std::mutex> lock(m_impl->observerMutex);
    m_impl->observerCb = cb;
    m_impl->observerUser = user;
}

FgRenderer::~FgRenderer()
{
    if (m_impl == nullptr)
    {
        return;
    }
    Impl& d = *m_impl;

    // Full FFX teardown (tolerates an already-unloaded loader — skips the ffx
    // calls and only releases COM then).
    TearDownFfxObjects(d);

    if (d.cbMapped != nullptr && d.cbUpload != nullptr)
    {
        d.cbUpload->Unmap(0, nullptr);
        d.cbMapped = nullptr;
    }
    d.cbUpload.Reset();
    d.srvHeap.Reset();
    d.pso.Reset();
    d.rootSig.Reset();

    if (d.fenceEvent != nullptr)
    {
        CloseHandle(d.fenceEvent);
        d.fenceEvent = nullptr;
    }
    d.fence.Reset();
    d.cmdList.Reset();
    d.allocator.Reset();
    d.factory.Reset();

    delete &d;
    m_impl = nullptr;
}

} // namespace catra

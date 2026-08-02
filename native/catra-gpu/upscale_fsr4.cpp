// upscale_fsr4.cpp — FSR 4 (FidelityFX SDK) ML upscale backend (ST-14).
//
// Two compile modes, selected by CMake:
//
//   * CATRA_HAS_FSR4 defined (SDK present via -DCATRA_FSR_SDK_ROOT):
//       real ffx::ContextUpscale implementation (DX12 compute, RDNA 4 ML).
//   * CATRA_HAS_FSR4 NOT defined (the default — the SDK is license-gated and
//       never vendored): an inert stub. Fsr4IsCompiled() == false,
//       Fsr4IsAvailable() == false, Create() == CATRA_ERR_NOT_IMPL. The bridge
//       downgrades FSR 4 requests to FSR 1 (upscale_fsr1.cpp), which is always
//       compiled and carries the MVP.
//
// The FidelityFX SDK is NOT downloaded by this repository. To build the real
// path, clone https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK into
// lib/fsr-sdk/ (gitignored) and configure with -DCATRA_FSR_SDK_ROOT pointing at
// it. See scripts/build-native.ps1 for the exact layout.
//
// ROOT-CAUSE / DESIGN NOTES
//   * FSR 4 is ML-based and RDNA 4-only; the definitive availability check is
//     "does a throw-away ffx context initialize on this adapter", preceded by a
//     cheap DXGI vendor/device-ID pre-filter (AMD 0x1002 + RDNA 4 gfx12 range).
//   * The DX12 device used by the context comes from catra::CreateD3D12Device
//     (the ST-15 interop seam, now implemented in d3d_interop.cpp): it returns
//     the shared bridge device once interop_init has run. Process obtains the
//     input via catra::ShareTexture (pooled GPU-GPU copy / zero-copy NT share)
//     and, because that resource is co-owned with the interop pool, brackets the
//     ffx dispatch with the consumer half of the keyed-mutex ping-pong (see
//     d3d_interop.h) so the read never races the pool's next copy on hardware.
//   * No exception escapes this TU. ffx returns error codes (ffx::ReturnCode);
//     every failure is mapped to a CATRA_ERR_* and logged.

#include "upscale_fsr4.h"
#include "d3d_interop.h"
#include "catra_gpu.h"

namespace catra {
void BackendLog(int level, const char* fmt, ...);
}

#if defined(CATRA_HAS_FSR4)

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

// FidelityFX SDK (ffx_api C++ wrapper). These headers resolve only when
// CATRA_FSR_SDK_ROOT is on the include path (CMake adds it with the define).
#include <ffx_api/ffx_api.hpp>
#include <ffx_api/ffx_upscale.hpp>
#include <ffx_api/dx12/ffx_api_dx12.hpp>

namespace catra {

namespace {

using Microsoft::WRL::ComPtr;

// AMD's PCI vendor id.
constexpr UINT kAmdVendorId = 0x1002;

// RDNA 4 (gfx12) device-ID range. The RX 9070 series reports device IDs in the
// 0x7550-0x75FF block (e.g. RX 9070 XT = 0x7550, RX 9070 = 0x7551). This is a
// HEURISTIC pre-filter only — new SKUs may extend the range — so the definitive
// check remains the ffx context probe in Fsr4IsAvailable.
bool LooksLikeRdna4(UINT deviceId)
{
    return deviceId >= 0x7550 && deviceId <= 0x75FF;
}

// Resolves the DXGI adapter behind the bridge's D3D11 device.
HRESULT QueryAdapterDesc(ID3D11Device* d3d11Device, DXGI_ADAPTER_DESC& outDesc)
{
    ComPtr<IDXGIDevice> dxgiDevice;
    HRESULT hr = d3d11Device->QueryInterface(IID_PPV_ARGS(dxgiDevice.GetAddressOf()));
    if (FAILED(hr))
    {
        return hr;
    }
    ComPtr<IDXGIAdapter> adapter;
    hr = dxgiDevice->GetAdapter(adapter.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }
    return adapter->GetDesc(&outDesc);
}

// Maps the bridge quality preset onto the FidelityFX enum (1:1 by design).
ffx::QualityMode ToFfxQuality(UpscaleQualityMode mode)
{
    switch (mode)
    {
        case UpscaleQualityMode::NativeAA:         return ffx::QualityMode::NativeAA;
        case UpscaleQualityMode::Quality:          return ffx::QualityMode::Quality;
        case UpscaleQualityMode::Balanced:         return ffx::QualityMode::Balanced;
        case UpscaleQualityMode::Performance:      return ffx::QualityMode::Performance;
        case UpscaleQualityMode::UltraPerformance: return ffx::QualityMode::UltraPerformance;
        default:                                   return ffx::QualityMode::Quality;
    }
}

int FfxToCatra(ffx::ReturnCode rc)
{
    return (rc == FFX_OK) ? CATRA_OK : CATRA_ERR_DEVICE;
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

    ffx::ContextUpscale context;
    UpscaleQualityMode quality = UpscaleQualityMode::Quality;
    bool contextValid = false;

    // Consumer half of the keyed-mutex ping-pong (ST-15). Starts at 0 and
    // alternates 0/1 per consumed frame, in lockstep with the pool producer's
    // g_frameKey, so Acquire(k) always waits on the matching Release(k).
    uint64_t consumerKey = 0;
};

Fsr4Upscaler::~Fsr4Upscaler() = default;

bool Fsr4IsCompiled()
{
    return true;
}

bool Fsr4IsAvailable(ID3D11Device* d3d11Device)
{
    if (d3d11Device == nullptr)
    {
        return false;
    }

    // 1. Cheap pre-filter: AMD + RDNA 4.
    DXGI_ADAPTER_DESC desc = {};
    HRESULT hr = QueryAdapterDesc(d3d11Device, desc);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_DEBUG, "upscale_fsr4: adapter query hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return false;
    }
    if (desc.VendorId != kAmdVendorId)
    {
        BackendLog(CATRA_LOG_INFO,
                   "upscale_fsr4: adapter vendor 0x%04X != AMD (0x1002) -> FSR 4 off",
                   desc.VendorId);
        return false;
    }
    if (!LooksLikeRdna4(desc.DeviceId))
    {
        BackendLog(CATRA_LOG_INFO,
                   "upscale_fsr4: device 0x%04X not in the RDNA 4 range -> FSR 4 off",
                   desc.DeviceId);
        return false;
    }

    // 2. Definitive probe: build a throw-away context on a D3D12 device for this
    //    adapter. CreateD3D12Device is the ST-15 seam (the shared interop device
    //    once interop_init has run, else a standalone device on the adapter).
    std::unique_ptr<Fsr4Upscaler> probe;
    int rc = Fsr4Upscaler::Create(d3d11Device, 64, 64, 128, 128,
                                  UpscaleQualityMode::Quality, probe);
    if (rc != CATRA_OK)
    {
        BackendLog(CATRA_LOG_INFO,
                   "upscale_fsr4: probe context create rc=%d -> FSR 4 unavailable", rc);
        return false;
    }
    BackendLog(CATRA_LOG_INFO, "upscale_fsr4: available (RDNA 4 + FidelityFX SDK)");
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

    // --- Command plumbing (FSR 4 dispatches on our command list) -----------
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
            D3D12_COMMAND_LIST_TYPE_COMPUTE, d.allocator.Get(), nullptr,
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

    // --- FidelityFX upscale context ----------------------------------------
    // maxRenderSize bounds the largest input the context will accept (>= src);
    // displaySize is the output. For this fixed offline job both are the dst
    // size, and the per-dispatch renderSize carries the actual src size.
    ffx::UpscaleContext::CreateDesc desc;
    desc.device = ffx::GetDeviceDX12(d.device.Get());
    desc.maxRenderSize = {static_cast<uint32_t>(dstW), static_cast<uint32_t>(dstH)};
    desc.displaySize = {static_cast<uint32_t>(dstW), static_cast<uint32_t>(dstH)};
    desc.qualityMode = ToFfxQuality(quality);
    desc.flags = 0;

    ffx::ReturnCode rc = d.context.Create(desc);
    if (rc != FFX_OK)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: ffx context create failed (rc=%d)",
                   static_cast<int>(rc));
        return CATRA_ERR_DEVICE;
    }
    d.contextValid = true;

    BackendLog(CATRA_LOG_INFO,
               "upscale_fsr4: context ready %dx%d -> %dx%d (quality=%d)",
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
    if (!d.contextValid)
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
    mutexGuard.key = d.consumerKey;
    srcRes.As(&mutexGuard.mutex); // S_FALSE when absent -> mutex stays null
    if (mutexGuard.mutex != nullptr)
    {
        int arc = interop_acquire(mutexGuard.mutex, mutexGuard.key, 5000);
        if (arc != CATRA_OK)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "upscale_fsr4: keyed-mutex acquire rc=%d (key=%llu)",
                       arc, static_cast<unsigned long long>(mutexGuard.key));
            return CATRA_ERR_DEVICE;
        }
        mutexGuard.held = true; // destructor releases on every exit from here
    }

    // --- Output texture (dstW x dstH) --------------------------------------
    D3D12_RESOURCE_DESC outDesc = {};
    outDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    outDesc.Width = static_cast<UINT>(m_dstW);
    outDesc.Height = static_cast<UINT>(m_dstH);
    outDesc.DepthOrArraySize = 1;
    outDesc.MipLevels = 1;
    outDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
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

    // --- Record + dispatch FSR 4 -------------------------------------------
    hr = d.allocator->Reset();
    if (SUCCEEDED(hr))
    {
        hr = d.cmdList->Reset(d.allocator.Get(), nullptr);
    }
    if (FAILED(hr))
    {
        return CATRA_ERR_DEVICE;
    }

    ffx::UpscaleContext::DispatchDesc dispatch;
    dispatch.commandList = ffx::GetCommandListDX12(d.cmdList.Get());
    dispatch.renderSize = {static_cast<uint32_t>(m_srcW), static_cast<uint32_t>(m_srcH)};
    dispatch.color = ffx::GetResourceDX12(srcRes.Get());
    dispatch.output = ffx::GetResourceDX12(dstRes.Get());
    dispatch.reset = false;

    ffx::ReturnCode rc = d.context.Dispatch(dispatch);
    d.cmdList->Close();
    if (rc != FFX_OK)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr4: dispatch failed (rc=%d)", static_cast<int>(rc));
        return CATRA_ERR_DEVICE;
    }

    ID3D12CommandList* lists[] = {d.cmdList.Get()};
    d.queue->ExecuteCommandLists(1, lists);

    ++d.fenceValue;
    hr = d.queue->Signal(d.fence.Get(), d.fenceValue);
    if (SUCCEEDED(hr) && d.fence->GetCompletedValue() < d.fenceValue)
    {
        hr = d.fence->SetEventOnCompletion(d.fenceValue, d.fenceEvent);
        if (SUCCEEDED(hr))
        {
            WaitForSingleObject(d.fenceEvent, INFINITE);
        }
    }
    if (FAILED(hr))
    {
        return CATRA_ERR_DEVICE;
    }

    *outDst = dstRes.Detach(); // caller owns the reference
    return CATRA_OK;
}

} // namespace catra

#else // !CATRA_HAS_FSR4 ----------------------------------------------------
//
// Inert stub: the FidelityFX SDK is license-gated and not vendored. Every entry
// point degrades gracefully so the bridge builds and loads, and the caller
// downgrades FSR 4 -> FSR 1 (always compiled). FSR 1 carries the Phase-2 MVP.

namespace catra {

// The class holds a std::unique_ptr<Impl>; the defaulted destructor below must
// see a COMPLETE Impl even in the stub build (unique_ptr's deleter requires it).
// The stub never populates m_impl, so an empty definition suffices.
struct Fsr4Upscaler::Impl {};

bool Fsr4IsCompiled()
{
    return false;
}

bool Fsr4IsAvailable(ID3D11Device*)
{
    return false;
}

int Fsr4Upscaler::Create(ID3D11Device*, int, int, int, int,
                         UpscaleQualityMode, std::unique_ptr<Fsr4Upscaler>& out)
{
    out.reset();
    BackendLog(CATRA_LOG_WARN,
               "upscale_fsr4: built without the FidelityFX SDK (CATRA_HAS_FSR4 "
               "undefined) -> pass -DCATRA_FSR_SDK_ROOT to enable FSR 4");
    return CATRA_ERR_NOT_IMPL;
}

int Fsr4Upscaler::Process(ID3D11Texture2D*, ID3D12Resource** outDst)
{
    if (outDst != nullptr)
    {
        *outDst = nullptr;
    }
    return CATRA_ERR_NOT_IMPL;
}

Fsr4Upscaler::~Fsr4Upscaler() = default;

} // namespace catra

#endif // CATRA_HAS_FSR4

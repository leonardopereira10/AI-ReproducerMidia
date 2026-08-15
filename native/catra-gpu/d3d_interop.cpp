// d3d_interop.cpp — D3D11 <-> DX12 texture sharing (ST-15).
//
// Implements the zero-copy NT-handle share, the pooled GPU-GPU copy fallback
// (round-robin, rebuilt only on resolution/format change), the keyed-mutex
// ping-pong primitives and the D3D12 device/queue lifecycle used by
// catra_gpu.cpp (catra_init / catra_shutdown). See d3d_interop.h for the
// protocol and ownership contract.
//
// All state is RAII (ComPtr + explicit CloseHandle in the pool teardown);
// nothing here throws by design, and the C ABI entry points that reach this
// TU are GuardCabi-wrapped in catra_gpu.cpp.

#include "d3d_interop.h"

#include <mutex>
#include <utility>
#include <vector>

namespace catra {

// Log shim defined in catra_gpu.cpp (same sink as the C ABI diagnostics).
void BackendLog(int level, const char* fmt, ...);

} // namespace catra

namespace {

using Microsoft::WRL::ComPtr;

// Frames in flight the pool is sized for (spec ST-15: 4-8). 4 covers the
// decoder -> interp -> upscale -> encode pipeline depth of the offline path.
constexpr size_t kInteropPoolSize = 4;

// Default keyed-mutex acquire timeout (spec ST-15: 5000 ms).
constexpr uint32_t kInteropAcquireTimeoutMs = 5000;

// Maps an HRESULT to a CATRA_ERR_* code. Timeouts (both spellings DXGI uses)
// surface as CATRA_ERR_DEVICE: a stalled share is a device-level fault from
// the caller's point of view.
int HrToCatra(HRESULT hr)
{
    if (SUCCEEDED(hr))
    {
        return CATRA_OK;
    }
    if (hr == E_INVALIDARG)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    if (hr == E_NOTIMPL)
    {
        return CATRA_ERR_NOT_IMPL;
    }
    return CATRA_ERR_DEVICE;
}

// Inverse map for the ST-13/14 compatibility wrappers, which speak HRESULT.
// CATRA_ERR_INIT maps to E_FAIL (not E_NOTIMPL): with ST-15 landed, a missing
// interop init is a genuine device fault, no longer "feature pending".
HRESULT CatraToHResult(int rc)
{
    switch (rc)
    {
        case CATRA_OK:            return S_OK;
        case CATRA_ERR_INVALID_ARG: return E_INVALIDARG;
        case CATRA_ERR_NOT_IMPL:  return E_NOTIMPL;
        default:                  return E_FAIL;
    }
}

bool IsAcquireTimeout(HRESULT hr)
{
    return hr == DXGI_ERROR_WAIT_TIMEOUT ||
           hr == HRESULT_FROM_WIN32(WAIT_TIMEOUT) ||
           // AMD driver quirk: IDXGIKeyedMutex::AcquireSync may return the raw
           // Win32 WAIT_TIMEOUT (258 / 0x102) as an HRESULT instead of wrapping
           // it via HRESULT_FROM_WIN32 (0x80070102) or the DXGI-specific code
           // (0x887B0001). Severity bit is clear -> FAILED() misses it. Observed
           // on RDNA 4 (Adrenalin 25.x) during the NV12<->BGRA pool-rebuild
           // sequence in the interpolation pipeline.
           hr == static_cast<HRESULT>(WAIT_TIMEOUT);
}

// --- Module state ----------------------------------------------------------
//
// Single offline pipeline: one frame loop drives the share path, so the whole
// share operation runs under g_mutex (pool reallocation must be exclusive
// with slot use). AcquireSync can block up to the timeout while the lock is
// held — acceptable for the single-writer offline design.

std::mutex g_mutex;

ComPtr<ID3D11Device> g_d3d11Device;        // retained (AddRef'd) at init
ComPtr<ID3D11DeviceContext> g_d3d11Context; // immediate context (copies)

ComPtr<ID3D12Device> g_d3d12Device;
ComPtr<ID3D12CommandQueue> g_d3d12Queue;         // DIRECT (spec)
ComPtr<ID3D12CommandAllocator> g_cmdAllocator;
ComPtr<ID3D12GraphicsCommandList> g_cmdList;     // created closed
ComPtr<ID3D12Fence> g_fence;
UINT64 g_fenceValue = 0;
HANDLE g_fenceEvent = nullptr;

// One pooled shared texture + its opened D3D12 counterpart + keyed mutex.
struct InteropPoolSlot
{
    ComPtr<ID3D11Texture2D> tex11;   // SHARED_KEYEDMUTEX | SHARED_NTHANDLE
    ComPtr<IDXGIKeyedMutex> mutex11; // QI'd from tex11 (same underlying mutex
                                     // the D3D12 side sees on res12)
    ComPtr<ID3D12Resource> res12;    // opened from ntHandle
    HANDLE ntHandle = nullptr;       // kept open for the pool's lifetime
};

std::vector<InteropPoolSlot> g_pool;
D3D11_TEXTURE2D_DESC g_poolDesc = {}; // geometry the pool was built for
size_t g_poolIndex = 0;               // round-robin cursor
uint64_t g_frameKey = 0;              // keyed-mutex key, alternates 0/1

// Monotonic pool generation: incremented exactly once per pool rebuild
// (EnsurePoolLocked). Consumers of pooled slots (FSR dispatch / AMF encode)
// poll this via interop_pool_generation() and reset their own ping-pong key
// to 0 whenever it changes: a rebuild mints fresh keyed mutexes and resets
// the producer key to 0, so a consumer that kept its old key would
// AcquireSync(k) against a Release(k) the rebuilt producer never issues
// (5 s timeout -> spurious CATRA_ERR_DEVICE — observed as the NV12->BGRA
// alternation rebuild in the interpolation pipeline).
uint64_t g_poolGeneration = 0;

// Derives the DXGI adapter from a D3D11 device and creates a D3D12 device on
// it (FEATURE_LEVEL_12_0 per spec). Shared handles REQUIRE the same adapter /
// LUID on both sides, which this guarantees by construction.
HRESULT CreateD3D12DeviceOnAdapter(ID3D11Device* d3d11Device, ID3D12Device** out)
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
    return D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0,
                             IID_PPV_ARGS(out));
}

// Geometry equality for pool reuse. Bind/usage/misc flags are intentionally
// ignored: the pool overrides them (SHADER_RESOURCE + shared NT) and
// CopyResource tolerates differing flags as long as the geometry matches.
bool SameGeometry(const D3D11_TEXTURE2D_DESC& a, const D3D11_TEXTURE2D_DESC& b)
{
    return a.Width == b.Width && a.Height == b.Height &&
           a.Format == b.Format && a.ArraySize == b.ArraySize &&
           a.MipLevels == b.MipLevels &&
           a.SampleDesc.Count == b.SampleDesc.Count &&
           a.SampleDesc.Quality == b.SampleDesc.Quality;
}

// Zero-copy share of an already NT-shareable D3D11 texture onto `d3d12Device`.
// Mints an NT handle on the source (IDXGIResource1::CreateSharedHandle) and
// opens it (ID3D12Device::OpenSharedHandle). The opened resource holds its own
// reference to the shared allocation, so the handle's lifetime is decoupled:
// when out_handle is null the handle is closed here, otherwise ownership
// passes to the caller (CloseHandle). Stateless — usable without interop_init.
HRESULT ShareD3D11ToD3D12Direct(ID3D12Device* d3d12Device,
                                ID3D11Texture2D* source,
                                ID3D12Resource** out,
                                HANDLE* out_handle)
{
    if (out_handle != nullptr)
    {
        *out_handle = nullptr;
    }

    ComPtr<IDXGIResource1> resource1;
    HRESULT hr = source->QueryInterface(IID_PPV_ARGS(resource1.GetAddressOf()));
    if (FAILED(hr))
    {
        return hr;
    }

    HANDLE handle = nullptr;
    hr = resource1->CreateSharedHandle(
        nullptr,
        DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
        nullptr,
        &handle);
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<ID3D12Resource> opened;
    hr = d3d12Device->OpenSharedHandle(handle, IID_PPV_ARGS(opened.GetAddressOf()));
    if (FAILED(hr))
    {
        CloseHandle(handle);
        return hr;
    }

    if (out_handle != nullptr)
    {
        *out_handle = handle; // caller owns (CloseHandle)
    }
    else
    {
        CloseHandle(handle); // the opened resource keeps the allocation alive
    }
    *out = opened.Detach();
    return S_OK;
}

// Destroys every pool slot: releases the D3D12 ref first, then the D3D11
// texture/mutex, then closes the NT handle. Caller holds g_mutex.
void DestroyPoolLocked()
{
    for (auto& slot : g_pool)
    {
        slot.res12.Reset();
        slot.mutex11.Reset();
        slot.tex11.Reset();
        if (slot.ntHandle != nullptr)
        {
            CloseHandle(slot.ntHandle);
            slot.ntHandle = nullptr;
        }
    }
    g_pool.clear();
    g_poolDesc = {};
    g_poolIndex = 0;
}

// Ensures the pool matches `srcDesc` (rebuilding it when the resolution /
// format changed — the ONLY trigger; never per frame). Each slot is a shared
// D3D11 texture paired with its D3D12 resource opened from the NT handle.
// Caller holds g_mutex. On failure the pool is left empty (clean state).
int EnsurePoolLocked(const D3D11_TEXTURE2D_DESC& srcDesc)
{
    if (!g_pool.empty() && SameGeometry(g_poolDesc, srcDesc))
    {
        return CATRA_OK;
    }

    DestroyPoolLocked();

    D3D11_TEXTURE2D_DESC sharedDesc = srcDesc;
    sharedDesc.Usage = D3D11_USAGE_DEFAULT;
    sharedDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE; // D3D12 side makes SRVs
    sharedDesc.CPUAccessFlags = 0;
    sharedDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX |
                           D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    std::vector<InteropPoolSlot> fresh;
    fresh.reserve(kInteropPoolSize);

    for (size_t i = 0; i < kInteropPoolSize; ++i)
    {
        InteropPoolSlot slot;
        HRESULT hr = g_d3d11Device->CreateTexture2D(
            &sharedDesc, nullptr, slot.tex11.GetAddressOf());
        if (SUCCEEDED(hr))
        {
            hr = slot.tex11.As(&slot.mutex11);
        }
        if (SUCCEEDED(hr))
        {
            ComPtr<IDXGIResource1> resource1;
            hr = slot.tex11.As(&resource1);
            if (SUCCEEDED(hr))
            {
                hr = resource1->CreateSharedHandle(
                    nullptr,
                    DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                    nullptr,
                    &slot.ntHandle);
            }
        }
        if (SUCCEEDED(hr))
        {
            hr = g_d3d12Device->OpenSharedHandle(
                slot.ntHandle, IID_PPV_ARGS(slot.res12.GetAddressOf()));
        }
        if (FAILED(hr))
        {
            if (slot.ntHandle != nullptr)
            {
                CloseHandle(slot.ntHandle);
            }
            for (auto& built : fresh)
            {
                built.res12.Reset();
                built.mutex11.Reset();
                built.tex11.Reset();
                if (built.ntHandle != nullptr)
                {
                    CloseHandle(built.ntHandle);
                }
            }
            catra::BackendLog(CATRA_LOG_ERROR,
                              "interop: pool slot %u create hr=0x%08lX",
                              static_cast<unsigned>(i),
                              static_cast<unsigned long>(hr));
            return CATRA_ERR_DEVICE;
        }
        fresh.push_back(std::move(slot));
    }

    g_pool = std::move(fresh);
    g_poolDesc = sharedDesc;
    g_poolIndex = 0;
    // Fresh slots have zeroed/unowned keyed mutexes. A stale g_frameKey (e.g.
    // 1 left over from the previous pool) would desync the producer/consumer
    // ping-pong: the producer would Release(1) while the consumer blocks in
    // AcquireSync(0) -> timeout -> deadlock after a format change.
    g_frameKey = 0;
    // Publish the rebuild so consumers reset their ping-pong key in lockstep
    // (see g_poolGeneration above).
    ++g_poolGeneration;
    catra::BackendLog(CATRA_LOG_INFO,
                      "interop: pool rebuilt %ux%u fmt=%u (%u slots)",
                      sharedDesc.Width, sharedDesc.Height,
                      static_cast<unsigned>(sharedDesc.Format),
                      static_cast<unsigned>(kInteropPoolSize));
    return CATRA_OK;
}

// Standalone (unpooled) fallback for ShareTexture when the interop module is
// not initialized or the caller passed a foreign D3D12 device: create a
// throw-away shared texture, GPU-copy the source into it, then share it
// zero-copy. The shared allocation outlives the temporary texture via the
// D3D12 reference. NOTE: no keyed-mutex release is issued here — consumers of
// this degraded path must fence-sync themselves (documented limitation; the
// initialized path does the full ping-pong).
HRESULT ShareViaTempCopy(ID3D11Device* d3d11Device,
                         ID3D12Device* d3d12Device,
                         ID3D11Texture2D* source,
                         ID3D12Resource** out)
{
    D3D11_TEXTURE2D_DESC sharedDesc;
    source->GetDesc(&sharedDesc);
    sharedDesc.Usage = D3D11_USAGE_DEFAULT;
    sharedDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    sharedDesc.CPUAccessFlags = 0;
    sharedDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX |
                           D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = d3d11Device->CreateTexture2D(
        &sharedDesc, nullptr, staging.GetAddressOf());
    if (FAILED(hr))
    {
        return hr;
    }

    ComPtr<ID3D11DeviceContext> context;
    d3d11Device->GetImmediateContext(context.GetAddressOf());
    context->CopyResource(staging.Get(), source);
    // Same submission guarantee as the pooled path below: without Flush the
    // copy may still sit in the immediate context's batched command stream
    // when the D3D12 side opens + reads the shared allocation -> cross-API
    // race -> GPU hang. This degraded path has no keyed-mutex release, so the
    // flush is the ONLY sync it gets.
    context->Flush();

    return ShareD3D11ToD3D12Direct(d3d12Device, staging.Get(), out, nullptr);
}

// NV12 -> BGRA conversion on the D3D11 side.
//
// The AMD RDNA 4 driver does not correctly share D3D11-CREATED NV12 (planar)
// textures into D3D12 via NT handles: the opened D3D12 resource reads zeros
// (verified with tools/interop_readback_test.cpp — BGRA shares cleanly, NV12
// does not). To keep the pooled share BGRA-only, NV12 decoder frames are
// converted to BGRA here before the pooled copy.
//
// Uses a full-texture CopyResource (proven reliable on this driver, unlike
// CopySubresourceRegion which yields zeros for NV12 planar sources) into a
// matching NV12 staging texture, then two separate Map calls for the Y and UV
// subresources of the target array slice. The Y/UV bytes are combined on the
// CPU (BT.601 full-range) into a BGRA staging texture, then copied to a
// shader-bindable BGRA GPU texture.
//
// `targetSlice` selects which array slice to convert (0 for single-slice
// textures, frame->data[1] for decoder texture arrays).
ComPtr<ID3D11Texture2D> ConvertNv12ToBgra11(ID3D11Texture2D* src,
                                            const D3D11_TEXTURE2D_DESC& srcDesc,
                                            unsigned int targetSlice = 0)
{
    const int w = static_cast<int>(srcDesc.Width);
    const int h = static_cast<int>(srcDesc.Height);

    // NV12 staging. Two shapes exist:
    //
    // (a) Single-slice source (ArraySize == 1): whole-texture CopyResource
    //     into a matching staging (proven reliable on AMD RDNA 4).
    //
    // (b) Decoder texture ARRAY (ArraySize > 1, e.g. FFmpeg D3D11VA pools with
    //     ArraySize=20 on AMD RDNA 4): a NV12 staging ARRAY cannot be created
    //     — plain CreateTexture2D of a planar-format array returns
    //     E_INVALIDARG (0x80070057) because NV12 arrays only exist with
    //     D3D11_BIND_DECODER, which is incompatible with USAGE_STAGING.
    //     Allocate a SINGLE-SLICE NV12 staging and copy the target slice with
    //     ONE CopySubresourceRegion using the slice index as the source
    //     subresource.
    //
    //     CRITICAL (learned from the playback path, VideoRenderer.cs
    //     ScaleHardwareToBgra — the ONLY verified-good NV12 read on this
    //     driver): for planar formats the subresource index is the ARRAY
    //     SLICE, NOT slice*2+plane. One CopySubresourceRegion(dst, 0, src,
    //     slice) copies Y AND UV together, and a single Map(subresource 0)
    //     exposes them contiguously (UV begins h*RowPitch bytes in, same
    //     pitch). Per-plane copies (src subresource slice*2+1) and separate
    //     Map()s of the UV "subresource" read ZEROS on AMD RDNA 4 — that
    //     split-subresource model is what produced the black/green frames.
    D3D11_TEXTURE2D_DESC stDesc = srcDesc;
    stDesc.Usage = D3D11_USAGE_STAGING;
    stDesc.BindFlags = 0;
    stDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stDesc.MiscFlags = 0;

    ComPtr<ID3D11Texture2D> nvStaging;
    HRESULT hr = E_FAIL;
    bool isSliceCopy = false;

    if (srcDesc.ArraySize <= 1)
    {
        hr = g_d3d11Device->CreateTexture2D(&stDesc, nullptr, nvStaging.GetAddressOf());
        if (SUCCEEDED(hr))
        {
            // CopyResource records the copy; Map below blocks until it completes.
            g_d3d11Context->CopyResource(nvStaging.Get(), src);
        }
    }
    else
    {
        // Array source: single-slice staging + whole-slice copy (slice index
        // as subresource — mirrors VideoRenderer.ScaleHardwareToBgra).
        stDesc.ArraySize = 1;
        hr = g_d3d11Device->CreateTexture2D(&stDesc, nullptr, nvStaging.GetAddressOf());
        if (SUCCEEDED(hr))
        {
            g_d3d11Context->CopySubresourceRegion(
                nvStaging.Get(), 0, 0, 0, 0, src, targetSlice, nullptr);
            isSliceCopy = true;
        }
    }

    if (FAILED(hr) || !nvStaging)
    {
        catra::BackendLog(CATRA_LOG_ERROR, "interop: NV12 staging alloc/copy hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return nullptr;
    }

    // Map the staging. For the slice-copy path the ONLY verified-good read on
    // this driver is a single Map(subresource 0) whose region spans Y then UV
    // contiguously (UV at h*RowPitch) — exactly what the playback renderer
    // does. Separate Y/UV plane maps read zeros on AMD RDNA 4, so the slice
    // path never uses them.
    bool singleRegionMap = isSliceCopy;
    D3D11_MAPPED_SUBRESOURCE mapY = {};
    D3D11_MAPPED_SUBRESOURCE mapUV = {};

    if (singleRegionMap)
    {
        hr = g_d3d11Context->Map(nvStaging.Get(), 0, D3D11_MAP_READ, 0, &mapY);
        if (FAILED(hr))
        {
            catra::BackendLog(CATRA_LOG_ERROR,
                       "interop: NV12 staging map hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            return nullptr;
        }
        mapUV.pData = static_cast<uint8_t*>(mapY.pData) +
                      static_cast<size_t>(h) * mapY.RowPitch;
        mapUV.RowPitch = mapY.RowPitch;
    }
    else
    {
        hr = g_d3d11Context->Map(nvStaging.Get(), 0, D3D11_MAP_READ, 0, &mapY);
        if (FAILED(hr))
        {
            catra::BackendLog(CATRA_LOG_ERROR,
                       "interop: NV12 staging map Y hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            return nullptr;
        }
        // Single-slice CopyResource path: try a separate UV map first; if the
        // driver rejects it (known RDNA 4 quirk) reuse the contiguous layout.
        hr = g_d3d11Context->Map(nvStaging.Get(), 1, D3D11_MAP_READ, 0, &mapUV);
        if (FAILED(hr))
        {
            singleRegionMap = true;
            mapUV.pData = static_cast<uint8_t*>(mapY.pData) +
                          static_cast<size_t>(h) * mapY.RowPitch;
            mapUV.RowPitch = mapY.RowPitch;
        }
    }

    // BGRA staging (CPU write) + destination GPU texture.
    D3D11_TEXTURE2D_DESC bgraStagingDesc = {};
    bgraStagingDesc.Width = srcDesc.Width;
    bgraStagingDesc.Height = srcDesc.Height;
    bgraStagingDesc.MipLevels = 1;
    bgraStagingDesc.ArraySize = 1;
    bgraStagingDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    bgraStagingDesc.SampleDesc.Count = 1;
    bgraStagingDesc.Usage = D3D11_USAGE_STAGING;
    bgraStagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    ComPtr<ID3D11Texture2D> bgraStaging;
    hr = g_d3d11Device->CreateTexture2D(&bgraStagingDesc, nullptr, bgraStaging.GetAddressOf());
    if (FAILED(hr))
    {
        if (singleRegionMap)
        {
            g_d3d11Context->Unmap(nvStaging.Get(), 0);
        }
        else
        {
            g_d3d11Context->Unmap(nvStaging.Get(), 1);
            g_d3d11Context->Unmap(nvStaging.Get(), 0);
        }
        catra::BackendLog(CATRA_LOG_ERROR, "interop: BGRA staging alloc hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return nullptr;
    }

    D3D11_MAPPED_SUBRESOURCE mapB = {};
    hr = g_d3d11Context->Map(bgraStaging.Get(), 0, D3D11_MAP_WRITE, 0, &mapB);
    if (FAILED(hr))
    {
        if (singleRegionMap)
        {
            g_d3d11Context->Unmap(nvStaging.Get(), 0);
        }
        else
        {
            g_d3d11Context->Unmap(nvStaging.Get(), 1);
            g_d3d11Context->Unmap(nvStaging.Get(), 0);
        }
        catra::BackendLog(CATRA_LOG_ERROR, "interop: BGRA staging map hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return nullptr;
    }

    const uint8_t* yBase = static_cast<const uint8_t*>(mapY.pData);
    const uint8_t* uvBase = static_cast<const uint8_t*>(mapUV.pData);
    const size_t yPitch = mapY.RowPitch;
    const size_t uvPitch = mapUV.RowPitch;

    // Diagnostic: log first Y/UV values to detect all-zero decoder output
    // (the AMD RDNA 4 driver bug where NV12 reads return zeros).
    catra::BackendLog(CATRA_LOG_INFO,
               "[nv12conv] slice=%u Y(0,0)=%u Y(10,0)=%u Y(mid)=%u U=%u V=%u "
               "yPitch=%zu uvPitch=%u",
               targetSlice,
               yBase[0], yBase[10],
               yBase[static_cast<size_t>(h / 2) * yPitch + 10],
               uvBase[0], uvBase[1],
               yPitch, static_cast<unsigned>(uvPitch));

    auto clamp8 = [](float v) -> uint8_t {
        return v < 0.0f ? 0 : (v > 255.0f ? 255 : static_cast<uint8_t>(v + 0.5f));
    };

    for (int y = 0; y < h; ++y)
    {
        const uint8_t* yPx = yBase + static_cast<size_t>(y) * yPitch;
        const uint8_t* uvPx = uvBase + static_cast<size_t>(y / 2) * uvPitch;
        uint8_t* dst = static_cast<uint8_t*>(mapB.pData) + static_cast<size_t>(y) * mapB.RowPitch;
        for (int x = 0; x < w; ++x)
        {
            float Y = yPx[x];
            float U = uvPx[(x / 2) * 2 + 0] - 128.0f;
            float V = uvPx[(x / 2) * 2 + 1] - 128.0f;
            // BT.601 full-range YUV -> RGB
            float R = Y + 1.402f * V;
            float G = Y - 0.344136f * U - 0.714136f * V;
            float B = Y + 1.772f * U;
            dst[x * 4 + 0] = clamp8(B);
            dst[x * 4 + 1] = clamp8(G);
            dst[x * 4 + 2] = clamp8(R);
            dst[x * 4 + 3] = 255;
        }
    }

    if (singleRegionMap)
    {
        g_d3d11Context->Unmap(nvStaging.Get(), 0);
    }
    else
    {
        g_d3d11Context->Unmap(nvStaging.Get(), 1);
        g_d3d11Context->Unmap(nvStaging.Get(), 0);
    }
    g_d3d11Context->Unmap(bgraStaging.Get(), 0);

    D3D11_TEXTURE2D_DESC gpuDesc = bgraStagingDesc;
    gpuDesc.Usage = D3D11_USAGE_DEFAULT;
    gpuDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    gpuDesc.CPUAccessFlags = 0;

    ComPtr<ID3D11Texture2D> dst11;
    hr = g_d3d11Device->CreateTexture2D(&gpuDesc, nullptr, dst11.GetAddressOf());
    if (FAILED(hr))
    {
        catra::BackendLog(CATRA_LOG_ERROR, "interop: BGRA GPU texture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return nullptr;
    }
    g_d3d11Context->CopyResource(dst11.Get(), bgraStaging.Get());
    return dst11;
}

// Blocks the CPU until every D3D11 command submitted so far has FINISHED on
// the GPU (not merely been submitted).
//
// Why this is needed (AMD RDNA 4, observed with tools/interop_readback_test):
// Flush() only pushes the immediate context's batched commands into the GPU
// queue; it does NOT wait for them to complete. The keyed-mutex ReleaseSync /
// D3D12-side AcquireSync is supposed to provide the cross-API completion
// guarantee, but on this driver the D3D12 consumer (AMF hardware encoder)
// reads the shared allocation while the D3D11 copy is still in flight when
// the GPU is busy (e.g. right after DirectML inference) -> the encoder sees a
// partially-written (top band) or zeroed (green) surface. Because this is an
// OFFLINE transcode pipeline (throughput-bound, not latency-bound), a CPU
// stall until the copy is verifiably done is the correct, robust trade-off.
void WaitForD3D11GpuIdle()
{
    D3D11_QUERY_DESC qd = {};
    qd.Query = D3D11_QUERY_EVENT;
    ComPtr<ID3D11Query> query;
    HRESULT hr = g_d3d11Device->CreateQuery(&qd, query.GetAddressOf());
    if (FAILED(hr))
    {
        return; // best-effort: fall back to flush-only behaviour
    }
    g_d3d11Context->End(query.Get());
    // Spin with a bounded timeout; GetData returns S_OK once the GPU reached
    // the End() marker. Sleep to avoid burning a core while we wait.
    for (int i = 0; i < 2000; ++i) // ~2s ceiling (2000 * 1ms)
    {
        BOOL done = FALSE;
        hr = g_d3d11Context->GetData(query.Get(), &done, sizeof(done), 0);
        if (hr == S_OK && done)
        {
            return;
        }
        if (hr != S_OK && hr != S_FALSE)
        {
            return; // device error: don't spin forever
        }
        Sleep(1);
    }
}

// ===========================================================================
// D3D12-native pool populated via CPU round-trip (robust encode path)
// ===========================================================================
//
// The AMD RDNA 4 driver proves unreliable for cross-API shared surfaces once
// DirectML has run (the D3D12 view of a D3D11-shared texture reads zeros or a
// partial top band -> green frames; keyed-mutex AcquireSync also returns
// non-standard timeouts). Because this is an OFFLINE transcode pipeline
// (throughput-bound), we trade zero-copy for correctness: read the D3D11
// source back to system memory, then upload it into a D3D12-NATIVE texture on
// our own queue. The AMF encoder then consumes a plain same-device D3D12
// resource — no shared handle, no keyed mutex, no cross-API view.

struct Pool12Slot
{
    ComPtr<ID3D12Resource> tex; // D3D12-native, DEFAULT heap
    // Real GPU state of the slot. Slots are recycled round-robin: after the
    // first upload the post-copy barrier leaves them in COMMON, so the next
    // use must transition COMMON->COPY_DEST before writing (a copy into a
    // COMMON resource, or a barrier whose StateBefore doesn't match the real
    // state, is undefined behaviour — the AMD driver can silently drop the
    // copy -> stale/zero pixels). Tracks the true state so UploadCpuTo12 can
    // issue the correct pre/post barriers.
    D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COPY_DEST; // initial create state
};

std::vector<Pool12Slot> g_pool12;
D3D11_TEXTURE2D_DESC g_pool12Desc = {};
size_t g_pool12Index = 0;

int EnsurePool12Locked(const D3D11_TEXTURE2D_DESC& d)
{
    if (!g_pool12.empty() && SameGeometry(g_pool12Desc, d))
    {
        return CATRA_OK;
    }
    g_pool12.clear();

    for (size_t i = 0; i < kInteropPoolSize; ++i)
    {
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width = d.Width;
        rd.Height = d.Height;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = d.Format;
        rd.SampleDesc.Count = 1;
        rd.SampleDesc.Quality = 0;
        rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags = D3D12_RESOURCE_FLAG_NONE;

        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_DEFAULT;

        Pool12Slot slot;
        HRESULT hr = g_d3d12Device->CreateCommittedResource(
            &hp, D3D12_HEAP_FLAG_NONE, &rd,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
            IID_PPV_ARGS(slot.tex.GetAddressOf()));
        if (FAILED(hr))
        {
            catra::BackendLog(CATRA_LOG_ERROR,
                              "interop: pool12 slot create hr=0x%08lX",
                              static_cast<unsigned long>(hr));
            g_pool12.clear();
            return CATRA_ERR_DEVICE;
        }
        g_pool12.push_back(std::move(slot));
    }

    g_pool12Desc = d;
    g_pool12Index = 0;
    ++g_poolGeneration;
    catra::BackendLog(CATRA_LOG_INFO,
                      "interop: pool12 rebuilt %ux%u fmt=%u (%u slots)",
                      d.Width, d.Height, static_cast<unsigned>(d.Format),
                      static_cast<unsigned>(kInteropPoolSize));
    return CATRA_OK;
}

// Logs the active pooled-share path once per transition (a per-frame log
// would spam the backend sink on a throughput-bound offline transcode).
// Caller holds g_mutex (the whole share runs under it), so the plain statics
// need no extra synchronization.
void LogSharePath(bool gpuGpu)
{
    static bool lastGpuGpu = false;
    static bool logged = false;
    if (logged && lastGpuGpu == gpuGpu)
    {
        return;
    }
    logged = true;
    lastGpuGpu = gpuGpu;
    catra::BackendLog(CATRA_LOG_INFO,
                      gpuGpu
                          ? "interop: using GPU-GPU pooled copy (BGRA, zero CPU)"
                          : "interop: using CPU round-trip (NV12 or GPU copy failed)");
}

// Reads a D3D11 texture back to tightly-packed system memory (BGRA).
bool ReadBack11ToCpu(ID3D11Texture2D* src, const D3D11_TEXTURE2D_DESC& d,
                     std::vector<uint8_t>& out, UINT& outPitch)
{
    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;

    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = g_d3d11Device->CreateTexture2D(&sd, nullptr, staging.GetAddressOf());
    if (FAILED(hr))
    {
        return false;
    }

    g_d3d11Context->CopyResource(staging.Get(), src);
    g_d3d11Context->Flush();
    WaitForD3D11GpuIdle();

    D3D11_MAPPED_SUBRESOURCE m = {};
    hr = g_d3d11Context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m);
    if (FAILED(hr))
    {
        return false;
    }

    const UINT bpp = 4; // BGRA
    out.resize(static_cast<size_t>(d.Width) * d.Height * bpp);
    const uint8_t* srcRows = static_cast<const uint8_t*>(m.pData);
    for (UINT y = 0; y < d.Height; ++y)
    {
        memcpy(out.data() + static_cast<size_t>(y) * d.Width * bpp,
               srcRows + static_cast<size_t>(y) * m.RowPitch,
               static_cast<size_t>(d.Width) * bpp);
    }
    outPitch = d.Width * bpp;
    g_d3d11Context->Unmap(staging.Get(), 0);
    // Always log readback pixels (diagnostic for black-frame issue)
    {
        size_t mid = static_cast<size_t>(d.Height / 2) * d.Width * bpp + 40;
        catra::BackendLog(CATRA_LOG_INFO,
                          "[readback] w=%u h=%u first4=%u,%u,%u,%u mid(row%u,x10)=%u,%u,%u,%u",
                          d.Width, d.Height, out[0], out[1], out[2], out[3], d.Height / 2,
                          out[mid], out[mid + 1], out[mid + 2], out[mid + 3]);
    }
    return true;
}

// Uploads tightly-packed BGRA bytes into a D3D12-native texture on our queue.
bool UploadCpuTo12(ID3D12Resource* dst, const D3D11_TEXTURE2D_DESC& d,
                   const std::vector<uint8_t>& data, UINT srcPitch,
                   D3D12_RESOURCE_STATES& slotState)
{
    D3D12_RESOURCE_DESC rd = dst->GetDesc();
    UINT numRows = 0;
    UINT64 rowSize = 0;
    UINT64 total = 0;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
    g_d3d12Device->GetCopyableFootprints(&rd, 0, 1, 0, &fp, &numRows, &rowSize, &total);

    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_UPLOAD;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width = total;
    bd.Height = 1;
    bd.DepthOrArraySize = 1;
    bd.MipLevels = 1;
    bd.Format = DXGI_FORMAT_UNKNOWN;
    bd.SampleDesc.Count = 1;
    bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

    ComPtr<ID3D12Resource> upload;
    HRESULT hr = g_d3d12Device->CreateCommittedResource(
        &hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ,
        nullptr, IID_PPV_ARGS(upload.GetAddressOf()));
    if (FAILED(hr))
    {
        return false;
    }

    uint8_t* mapped = nullptr;
    hr = upload->Map(0, nullptr, reinterpret_cast<void**>(&mapped));
    if (FAILED(hr))
    {
        return false;
    }
    const UINT dstPitch = fp.Footprint.RowPitch;
    const UINT rowBytes = static_cast<UINT>(srcPitch); // tightly packed = width*4
    for (UINT y = 0; y < d.Height; ++y)
    {
        memcpy(mapped + fp.Offset + static_cast<size_t>(y) * dstPitch,
               data.data() + static_cast<size_t>(y) * srcPitch,
               rowBytes);
    }
    upload->Unmap(0, nullptr);

    // Record + execute the upload copy on our DIRECT queue, then wait.
    HRESULT rs = g_cmdAllocator->Reset();
    if (SUCCEEDED(rs)) rs = g_cmdList->Reset(g_cmdAllocator.Get(), nullptr);
    if (FAILED(rs)) return false;

    D3D12_TEXTURE_COPY_LOCATION dstLoc = {};
    dstLoc.pResource = dst;
    // 0 = subresource (SDK …_SUBRESOURCE / directx-headers …_SUBRESOURCE_INDEX).
    dstLoc.Type = static_cast<D3D12_TEXTURE_COPY_TYPE>(0);
    dstLoc.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
    srcLoc.pResource = upload.Get();
    srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    srcLoc.PlacedFootprint = fp;
    // Pre-barrier: the slot may currently be in COMMON (left there by the
    // previous frame's post-copy transition). Copying into a COMMON resource
    // is undefined behaviour, so first transition (slotState)->COPY_DEST only
    // when the tracked state says it's needed. Fresh slots are created in
    // COPY_DEST and need no barrier on their first use.
    if (slotState != D3D12_RESOURCE_STATE_COPY_DEST)
    {
        D3D12_RESOURCE_BARRIER pre = {};
        pre.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        pre.Transition.pResource = dst;
        pre.Transition.StateBefore = slotState;
        pre.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
        pre.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        g_cmdList->ResourceBarrier(1, &pre);
        slotState = D3D12_RESOURCE_STATE_COPY_DEST;
    }

    g_cmdList->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, nullptr);

    // Post-barrier: COPY_DEST -> COMMON so the AMF encoder can consume it.
    D3D12_RESOURCE_BARRIER bar = {};
    bar.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    bar.Transition.pResource = dst;
    bar.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    bar.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    bar.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    g_cmdList->ResourceBarrier(1, &bar);
    slotState = D3D12_RESOURCE_STATE_COMMON;
    g_cmdList->Close();

    ID3D12CommandList* lists[] = { g_cmdList.Get() };
    g_d3d12Queue->ExecuteCommandLists(1, lists);

    const UINT64 fv = ++g_fenceValue;
    g_d3d12Queue->Signal(g_fence.Get(), fv);
    if (g_fence->GetCompletedValue() < fv)
    {
        g_fence->SetEventOnCompletion(fv, g_fenceEvent);
        WaitForSingleObject(g_fenceEvent, 5000);
    }
    return true;
}

// ===========================================================================
// AMF encode workaround helpers (SPRINT_04 subtask 03)
// ===========================================================================
//
// AMFContext2::CreateSurfaceFromDX12Native rejects USER-CREATED D3D12 textures
// (the FSR 1 / FSR 4 upscale outputs — docs/FSR_AMF_ISSUE_CONTEXT.md) but
// accepts the pool-slot resources opened from D3D11 shared textures. The
// workaround copies any D3D12 encode input into the pooled D3D11 path and
// hands the encoder the pool slot instead.

// Cached SHARED D3D12 staging texture for non-shareable encode inputs
// (generic case: FSR 4 output lives on a D3D12_HEAP_FLAG_NONE heap). Recreated
// only on geometry/format change; released by interop_shutdown.
ComPtr<ID3D12Resource> g_encodeStaging12;
D3D12_RESOURCE_DESC g_encodeStagingDesc = {};

int EnsureEncodeStagingLocked(const D3D12_RESOURCE_DESC& srcDesc)
{
    if (g_encodeStaging12 &&
        g_encodeStagingDesc.Width == srcDesc.Width &&
        g_encodeStagingDesc.Height == srcDesc.Height &&
        g_encodeStagingDesc.Format == srcDesc.Format)
    {
        return CATRA_OK;
    }
    g_encodeStaging12.Reset();
    g_encodeStagingDesc = {};

    D3D12_RESOURCE_DESC sd = {};
    sd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    sd.Width = srcDesc.Width;
    sd.Height = srcDesc.Height;
    sd.DepthOrArraySize = 1;
    sd.MipLevels = 1;
    sd.Format = srcDesc.Format;
    sd.SampleDesc.Count = 1;
    sd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    // Copy target only, but D3D11's OpenSharedResource1 rejects shared
    // textures that would map to ZERO bind flags (E_INVALIDARG, verified on
    // RDNA 4): a D3D12 Flags=NONE texture opens as a BindFlags=0 D3D11
    // texture, which is only legal for USAGE_STAGING. ALLOW_RENDER_TARGET
    // gives the opened D3D11 view a legal binding (BGRA is RTV-capable).
    sd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;

    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;

    // Initial state COMMON: the workaround contract requires inputs in COMMON,
    // and every copy round leaves the staging back in COMMON.
    const HRESULT hr = g_d3d12Device->CreateCommittedResource(
        &hp, D3D12_HEAP_FLAG_SHARED, &sd,
        D3D12_RESOURCE_STATE_COMMON, nullptr,
        IID_PPV_ARGS(g_encodeStaging12.GetAddressOf()));
    if (FAILED(hr))
    {
        catra::BackendLog(CATRA_LOG_ERROR,
                          "interop: encode staging create hr=0x%08lX",
                          static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    g_encodeStagingDesc = sd;
    catra::BackendLog(CATRA_LOG_INFO,
                      "interop: encode staging rebuilt %llux%u fmt=%u (shared)",
                      static_cast<unsigned long long>(sd.Width), sd.Height,
                      static_cast<unsigned>(sd.Format));
    return CATRA_OK;
}

// GPU copy src -> staging on the module's DIRECT queue + bounded fence wait.
// Both resources are taken and left in D3D12_RESOURCE_STATE_COMMON (the src
// contract is COMMON; the pre/post barriers make the round-trip explicit).
// Caller holds g_mutex. Returns CATRA_OK / CATRA_ERR_DEVICE (timeout is a
// device fault — NEVER INFINITE: a wedged queue must fail, not hang).
int CopyD3D12ToStagingLocked(ID3D12Resource* src, ID3D12Resource* dst)
{
    HRESULT rs = g_cmdAllocator->Reset();
    if (SUCCEEDED(rs))
    {
        rs = g_cmdList->Reset(g_cmdAllocator.Get(), nullptr);
    }
    if (FAILED(rs))
    {
        catra::BackendLog(CATRA_LOG_ERROR,
                          "interop: encode staging cmd reset hr=0x%08lX",
                          static_cast<unsigned long>(rs));
        return CATRA_ERR_DEVICE;
    }

    D3D12_RESOURCE_BARRIER pre[2] = {};
    pre[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    pre[0].Transition.pResource = src;
    pre[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    pre[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    pre[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    pre[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    pre[1].Transition.pResource = dst;
    pre[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    pre[1].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    pre[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    g_cmdList->ResourceBarrier(2, pre);

    // Whole-texture copy: geometries are identical by construction (the
    // staging is rebuilt from the source desc).
    D3D12_TEXTURE_COPY_LOCATION dstLoc = {};
    dstLoc.pResource = dst;
    dstLoc.Type = static_cast<D3D12_TEXTURE_COPY_TYPE>(0);
    dstLoc.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
    srcLoc.pResource = src;
    srcLoc.Type = static_cast<D3D12_TEXTURE_COPY_TYPE>(0);
    srcLoc.SubresourceIndex = 0;
    g_cmdList->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, nullptr);

    D3D12_RESOURCE_BARRIER post[2] = {};
    post[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    post[0].Transition.pResource = src;
    post[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    post[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    post[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    post[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    post[1].Transition.pResource = dst;
    post[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    post[1].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    post[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    g_cmdList->ResourceBarrier(2, post);

    rs = g_cmdList->Close();
    if (FAILED(rs))
    {
        catra::BackendLog(CATRA_LOG_ERROR,
                          "interop: encode staging cmd close hr=0x%08lX",
                          static_cast<unsigned long>(rs));
        return CATRA_ERR_DEVICE;
    }

    ID3D12CommandList* lists[] = { g_cmdList.Get() };
    g_d3d12Queue->ExecuteCommandLists(1, lists);

    const UINT64 fv = ++g_fenceValue;
    g_d3d12Queue->Signal(g_fence.Get(), fv);
    if (g_fence->GetCompletedValue() < fv)
    {
        g_fence->SetEventOnCompletion(fv, g_fenceEvent);
        if (WaitForSingleObject(g_fenceEvent, 5000) != WAIT_OBJECT_0)
        {
            catra::BackendLog(CATRA_LOG_ERROR,
                              "interop: encode staging fence wait timed out (5000 ms)");
            return CATRA_ERR_DEVICE;
        }
    }
    return CATRA_OK;
}

// GPU-GPU pooled copy of a D3D11 source into the next round-robin pool slot,
// extracted verbatim from interop_share_d3d11_to_d3d12 so the AMF encode
// workaround (interop_copy_d3d12_for_encode) can reuse it. Caller holds
// g_mutex. Returns CATRA_OK (the slot's D3D12 resource AddRef'd into
// *out_d3d12_tex) or CATRA_ERR_DEVICE on pool-ensure / ReleaseSync failure —
// interop_share_d3d11_to_d3d12 translates that into its CPU round-trip
// fallback (behaviour unchanged by the extraction).
int PooledCopyD3D11Locked(ID3D11Texture2D* src,
                          const D3D11_TEXTURE2D_DESC& desc,
                          ID3D12Resource** out_d3d12_tex)
{
    *out_d3d12_tex = nullptr;

    const int grc = EnsurePoolLocked(desc);
    if (grc != CATRA_OK || g_pool.empty())
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "interop: GPU-GPU pool ensure failed rc=%d — falling back to CPU round-trip",
                          grc);
        return CATRA_ERR_DEVICE;
    }

    InteropPoolSlot& slot = g_pool[g_poolIndex];
    g_poolIndex = (g_poolIndex + 1) % g_pool.size();

    g_d3d11Context->CopyResource(slot.tex11.Get(), src);
    // Flush submits the copy; WaitForD3D11GpuIdle blocks until it is FINISHED
    // on the GPU (AMD RDNA 4 needs completion, not just submission, before the
    // D3D12 consumer reads the shared allocation — see WaitForD3D11GpuIdle).
    g_d3d11Context->Flush();
    WaitForD3D11GpuIdle();

    // Producer half of the keyed-mutex ping-pong (spec ST-15): ReleaseSync(key)
    // hands the slot to the consumer, which does AcquireSync(key) before
    // reading (encode_amf / upscale_fsr*). The key alternates 0/1; a pool
    // rebuild resets it to 0 and bumps the generation so consumers resync in
    // lockstep. The toggle happens only on success — a failed release keeps
    // the key deterministic.
    const uint64_t key = g_frameKey;
    const HRESULT relHr = slot.mutex11->ReleaseSync(key);
    if (SUCCEEDED(relHr))
    {
        g_frameKey ^= 1;
        LogSharePath(true);
        *out_d3d12_tex = slot.res12.Get();
        slot.res12->AddRef();
        return CATRA_OK;
    }
    catra::BackendLog(CATRA_LOG_WARN,
                      "interop: GPU-GPU copy ReleaseSync(key=%llu) hr=0x%08lX — falling back to CPU round-trip",
                      static_cast<unsigned long long>(key),
                      static_cast<unsigned long>(relHr));
    return CATRA_ERR_DEVICE;
}

// Logs the encode-workaround route once per transition (per-frame logging
// would spam the backend sink on a throughput-bound offline transcode).
// Caller holds g_mutex.
void LogEncodeWorkaroundPath(bool staged)
{
    static bool lastStaged = false;
    static bool logged = false;
    if (logged && lastStaged == staged)
    {
        return;
    }
    logged = true;
    lastStaged = staged;
    catra::BackendLog(CATRA_LOG_INFO,
                      staged
                          ? "interop: encode workaround: D3D12 input non-shareable -> staging copy -> D3D11 pool path"
                          : "interop: encode workaround: D3D12 input shareable -> direct D3D11 open -> pool path");
}

} // namespace

namespace catra {

// ===========================================================================
// NV12 → BGRA staging conversion (callable from other TUs)
// ===========================================================================
//
// Thin wrapper around the anonymous-namespace ConvertNv12ToBgra11, exposed
// so catra_gpu.cpp can offer it as a C ABI entry point (the AMD RDNA 4
// workaround that bypasses av_hwframe_transfer_data).
ComPtr<ID3D11Texture2D> Nv12ToBgraStaging(ID3D11Texture2D* src,
                                          const D3D11_TEXTURE2D_DESC& srcDesc,
                                          unsigned int targetSlice)
{
    return ConvertNv12ToBgra11(src, srcDesc, targetSlice);
}

// ===========================================================================
// Lifecycle
// ===========================================================================

int interop_init(ID3D11Device* d3d11_device,
                 ID3D12Device** out_d3d12_device,
                 ID3D12CommandQueue** out_cmd_queue)
{
    if (out_d3d12_device != nullptr)
    {
        *out_d3d12_device = nullptr;
    }
    if (out_cmd_queue != nullptr)
    {
        *out_cmd_queue = nullptr;
    }
    if (d3d11_device == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_mutex);

    if (g_d3d12Device)
    {
        // Idempotent: hand out the existing device/queue (AddRef'd). The
        // bridge is single-device; a differing d3d11_device is ignored.
        if (out_d3d12_device != nullptr)
        {
            *out_d3d12_device = g_d3d12Device.Get();
            g_d3d12Device->AddRef();
        }
        if (out_cmd_queue != nullptr && g_d3d12Queue)
        {
            *out_cmd_queue = g_d3d12Queue.Get();
            g_d3d12Queue->AddRef();
        }
        return CATRA_OK;
    }

    // Build everything into locals first; commit only on full success so a
    // mid-way failure leaves no partial global state (RAII drops the rest).
    ComPtr<ID3D12Device> device;
    HRESULT hr = CreateD3D12DeviceOnAdapter(d3d11_device, device.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop_init: D3D12CreateDevice hr=0x%08lX (FL 12_0, same adapter)",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    ComPtr<ID3D12CommandQueue> queue;
    D3D12_COMMAND_QUEUE_DESC queueDesc = {};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT; // spec: DIRECT
    queueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
    hr = device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(queue.GetAddressOf()));

    ComPtr<ID3D12CommandAllocator> allocator;
    if (SUCCEEDED(hr))
    {
        hr = device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(allocator.GetAddressOf()));
    }

    ComPtr<ID3D12GraphicsCommandList> cmdList;
    if (SUCCEEDED(hr))
    {
        hr = device->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr,
            IID_PPV_ARGS(cmdList.GetAddressOf()));
        if (SUCCEEDED(hr))
        {
            cmdList->Close(); // parked; backends reset their own lists
        }
    }

    ComPtr<ID3D12Fence> fence;
    if (SUCCEEDED(hr))
    {
        hr = device->CreateFence(0, D3D12_FENCE_FLAG_NONE,
                                 IID_PPV_ARGS(fence.GetAddressOf()));
    }

    HANDLE fenceEvent = nullptr;
    if (SUCCEEDED(hr))
    {
        fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (fenceEvent == nullptr)
        {
            hr = HRESULT_FROM_WIN32(GetLastError());
        }
    }

    if (FAILED(hr))
    {
        if (fenceEvent != nullptr)
        {
            CloseHandle(fenceEvent);
        }
        BackendLog(CATRA_LOG_ERROR,
                   "interop_init: command plumbing hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    // Commit.
    g_d3d11Device = d3d11_device;
    d3d11_device->GetImmediateContext(g_d3d11Context.GetAddressOf());
    g_d3d12Device = std::move(device);
    g_d3d12Queue = std::move(queue);
    g_cmdAllocator = std::move(allocator);
    g_cmdList = std::move(cmdList);
    g_fence = std::move(fence);
    g_fenceValue = 0;
    g_fenceEvent = fenceEvent;
    g_frameKey = 0;

    if (out_d3d12_device != nullptr)
    {
        *out_d3d12_device = g_d3d12Device.Get();
        g_d3d12Device->AddRef();
    }
    if (out_cmd_queue != nullptr)
    {
        *out_cmd_queue = g_d3d12Queue.Get();
        g_d3d12Queue->AddRef();
    }

    BackendLog(CATRA_LOG_INFO,
               "interop_init: D3D12 device + DIRECT queue ready (adapter shared with D3D11)");
    return CATRA_OK;
}

void interop_shutdown()
{
    std::lock_guard<std::mutex> lock(g_mutex);

    // Pool textures reference both devices: drop them first, then the D3D12
    // plumbing, then the retained D3D11 device/context. No D3D12 work is ever
    // submitted on g_d3d12Queue by this module (the command list parks closed
    // and backends own their own queues), so no fence flush is needed here.
    DestroyPoolLocked();
    // D3D12-native pool (CPU round-trip encode path) holds ComPtrs to the
    // D3D12 device. Clear it here too, otherwise a re-init reuses slots from a
    // destroyed device (EnsurePool12Locked only checks SameGeometry and would
    // hand back stale resources -> zeros/crash).
    g_pool12.clear();
    g_pool12Desc = {};
    g_pool12Index = 0;
    // Cached encode-workaround staging (D3D12 shared) — same rationale.
    g_encodeStaging12.Reset();
    g_encodeStagingDesc = {};

    if (g_fenceEvent != nullptr)
    {
        CloseHandle(g_fenceEvent);
        g_fenceEvent = nullptr;
    }
    g_fence.Reset();
    g_cmdList.Reset();
    g_cmdAllocator.Reset();
    g_d3d12Queue.Reset();
    g_d3d12Device.Reset();
    g_d3d11Context.Reset();
    g_d3d11Device.Reset();
    g_fenceValue = 0;
    g_frameKey = 0;
    g_poolGeneration = 0;
}

// ===========================================================================
// Sharing
// ===========================================================================

int interop_share_d3d11_to_d3d12(ID3D11Texture2D* src,
                                 ID3D12Resource** out_d3d12_tex,
                                 HANDLE* out_shared_handle)
{
    if (out_d3d12_tex != nullptr)
    {
        *out_d3d12_tex = nullptr;
    }
    if (out_shared_handle != nullptr)
    {
        *out_shared_handle = nullptr;
    }
    if (src == nullptr || out_d3d12_tex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_mutex);

    if (!g_d3d12Device)
    {
        return CATRA_ERR_INIT;
    }

    D3D11_TEXTURE2D_DESC desc;
    src->GetDesc(&desc);

    const bool ntShared =
        (desc.MiscFlags & D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX) != 0 &&
        (desc.MiscFlags & D3D11_RESOURCE_MISC_SHARED_NTHANDLE) != 0;

    if (ntShared)
    {
        // ZERO-COPY: the source is already NT-shareable (keyed mutex + NT
        // handle). Mint + open; the consumer syncs via the keyed mutex QI'd
        // from the returned resource (FFmpeg releases keys on its schedule).
        HRESULT hr = ShareD3D11ToD3D12Direct(g_d3d12Device.Get(), src,
                                             out_d3d12_tex, out_shared_handle);
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR,
                       "interop: zero-copy share hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            return HrToCatra(hr);
        }
        return CATRA_OK;
    }

    // POOLED COPY: source is not NT-shareable (typical D3D11VA decoder
    // texture). Since ST-23 the pipeline is 100% BGRA (decoder frames leave
    // the GPU shader as BGRA, RIFE emits BGRA), and BGRA cross-API sharing is
    // verified clean on the AMD RDNA 4 driver — so BGRA frames take the
    // pooled GPU-GPU copy below (ST-24): one CopyResource into the next
    // round-robin shared slot, zero CPU traffic, no per-frame allocation.
    // NV12 (should not happen post-ST-23, kept for safety) and any GPU-GPU
    // failure fall back to the CPU round-trip into g_pool12.
    if (desc.Format != DXGI_FORMAT_NV12)
    {
        // BGRA GPU-GPU path: copy into the pooled shared D3D11 texture, sync
        // cross-API, then hand out the paired D3D12 resource (opened from the
        // NT handle at pool-build time) to the consumer (AMF wraps it via
        // CreateSurfaceFromDX12Native and QIs the keyed mutex off it). The
        // copy segment lives in PooledCopyD3D11Locked (shared with the AMF
        // encode workaround); a failure there falls through to the CPU
        // round-trip below — behaviour identical to the pre-refactor code.
        ID3D12Resource* pooled = nullptr;
        if (PooledCopyD3D11Locked(src, desc, &pooled) == CATRA_OK)
        {
            // No per-frame NT handle is minted (the pool keeps its handles
            // open for its whole lifetime); leave *out_shared_handle null
            // so the caller's RAII cleanup skips CloseHandle.
            *out_d3d12_tex = pooled;
            return CATRA_OK;
        }
    }

    // NV12 path (converted to BGRA first — the AMD RDNA 4 driver does not
    // correctly share D3D11-created NV12 planar textures into D3D12; the
    // D3D12 view reads zeros -> green frames) and BGRA GPU-GPU fallback:
    // CPU ROUND-TRIP into a D3D12-native pool slot (see header comment above
    // Pool12Slot). Read the (BGRA) D3D11 source back to system memory, upload
    // into a same-device D3D12 texture, hand that to the consumer.
    ComPtr<ID3D11Texture2D> converted;
    ID3D11Texture2D* copySrc = src;
    if (desc.Format == DXGI_FORMAT_NV12)
    {
        converted = ConvertNv12ToBgra11(src, desc);
        if (converted == nullptr)
        {
            return CATRA_ERR_DEVICE;
        }
        copySrc = converted.Get();
        copySrc->GetDesc(&desc); // pool geometry now matches the BGRA texture
    }
    LogSharePath(false);

    int rc = EnsurePool12Locked(desc);
    if (rc != CATRA_OK)
    {
        return rc;
    }

    Pool12Slot& slot = g_pool12[g_pool12Index];
    g_pool12Index = (g_pool12Index + 1) % g_pool12.size();

    std::vector<uint8_t> cpu;
    UINT srcPitch = 0;
    if (!ReadBack11ToCpu(copySrc, desc, cpu, srcPitch))
    {
        BackendLog(CATRA_LOG_ERROR, "interop: pool12 D3D11 readback failed");
        return CATRA_ERR_DEVICE;
    }
    if (!UploadCpuTo12(slot.tex.Get(), desc, cpu, srcPitch, slot.state))
    {
        BackendLog(CATRA_LOG_ERROR, "interop: pool12 D3D12 upload failed");
        return CATRA_ERR_DEVICE;
    }

    // No shared NT handle is minted on this path (the resource never leaves
    // the D3D12 device); leave *out_shared_handle null so the caller's RAII
    // cleanup skips CloseHandle.
    *out_d3d12_tex = slot.tex.Get();
    slot.tex->AddRef();
    return CATRA_OK;
}

int interop_share_d3d12_to_d3d11(ID3D12Resource* src,
                                 ID3D11Device* d3d11_device,
                                 ID3D11Texture2D** out_d3d11_tex)
{
    if (out_d3d11_tex != nullptr)
    {
        *out_d3d11_tex = nullptr;
    }
    if (src == nullptr || d3d11_device == nullptr || out_d3d11_tex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }

    // The creating device travels with the resource; mint the NT handle there.
    ComPtr<ID3D12Device> device12;
    HRESULT hr = src->GetDevice(IID_PPV_ARGS(device12.GetAddressOf()));
    if (FAILED(hr))
    {
        return HrToCatra(hr);
    }

    HANDLE handle = nullptr;
    // D3D12 CreateSharedHandle REQUIRES dwAccess == GENERIC_ALL (the D3D11
    // DXGI_SHARED_RESOURCE_READ|WRITE flags fail with E_INVALIDARG — verified
    // via the D3D12 debug layer, SPRINT_04 subtask 03).
    hr = device12->CreateSharedHandle(
        src,
        nullptr,
        GENERIC_ALL,
        nullptr,
        &handle);
    if (FAILED(hr))
    {
        // Most common cause: the resource was not created on a shared heap
        // (D3D12_HEAP_FLAG_SHARED / _SHARED_CROSS_ADAPTER). Nothing to fix
        // here — the producer must opt in at creation time.
        BackendLog(CATRA_LOG_ERROR,
                   "interop: D3D12 CreateSharedHandle hr=0x%08lX (resource not shareable?)",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    // OpenSharedResource1 (NT handles) lives on ID3D11Device1.
    ComPtr<ID3D11Device1> device11_1;
    hr = d3d11_device->QueryInterface(IID_PPV_ARGS(device11_1.GetAddressOf()));
    if (SUCCEEDED(hr))
    {
        ComPtr<ID3D11Texture2D> tex;
        hr = device11_1->OpenSharedResource1(handle, IID_PPV_ARGS(tex.GetAddressOf()));
        if (SUCCEEDED(hr))
        {
            *out_d3d11_tex = tex.Detach(); // caller owns; keyed mutex via QI
        }
    }
    CloseHandle(handle); // open consumed; the resource holds its own ref
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: OpenSharedResource1 hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }
    return CATRA_OK;
}

int interop_copy_d3d12_for_encode(ID3D12Resource* src,
                                  ID3D12Resource** out_pool_tex,
                                  HANDLE* out_shared_handle)
{
    if (out_pool_tex != nullptr)
    {
        *out_pool_tex = nullptr;
    }
    if (out_shared_handle != nullptr)
    {
        *out_shared_handle = nullptr;
    }
    if (src == nullptr || out_pool_tex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_mutex);

    if (!g_d3d11Device || !g_d3d11Context || !g_d3d12Device || !g_d3d12Queue ||
        !g_cmdAllocator || !g_cmdList || !g_fence || g_fenceEvent == nullptr)
    {
        return CATRA_ERR_INIT;
    }

    // Same-device contract: the staging copy + pool machinery run on the
    // interop device. Backends obtain their device via CreateD3D12Device,
    // which hands out the interop device once interop_init has run, so this
    // holds for every upscale output; a foreign device cannot be copied
    // cross-device (D3D12 CopyResource is same-device only).
    ComPtr<ID3D12Device> srcDevice;
    if (FAILED(src->GetDevice(IID_PPV_ARGS(srcDevice.GetAddressOf()))) ||
        srcDevice.Get() != g_d3d12Device.Get())
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: encode workaround rejected — src on a foreign D3D12 device");
        return CATRA_ERR_DEVICE;
    }

    // Defensive geometry guard: upscale outputs are always simple 2D textures;
    // anything else is a caller bug (fail clean instead of a device removal).
    const D3D12_RESOURCE_DESC d12 = src->GetDesc();
    if (d12.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
        d12.MipLevels != 1 || d12.DepthOrArraySize != 1 ||
        d12.SampleDesc.Count != 1)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: encode workaround invalid geometry (dim=%u mip=%u arr=%u samples=%u)",
                   static_cast<unsigned>(d12.Dimension), d12.MipLevels,
                   d12.DepthOrArraySize, d12.SampleDesc.Count);
        return CATRA_ERR_INVALID_ARG;
    }

    // --- Shareability probe: CreateSharedHandle succeeds only on a shared
    // heap. Failure is the GENERIC case (FSR 4 output: HEAP_FLAG_NONE) and
    // routes through the cached shared staging instead. A shareable texture
    // whose D3D11 open still fails (e.g. D3D11-incompatible resource flags)
    // ALSO falls back to the staging copy.
    ID3D12Resource* shareableSrc = nullptr;
    bool staged = false;
    HANDLE probeHandle = nullptr;
    ComPtr<ID3D11Texture2D> view11;
    HRESULT hr = g_d3d12Device->CreateSharedHandle(
        src,
        nullptr,
        GENERIC_ALL,
        nullptr,
        &probeHandle);
    if (SUCCEEDED(hr))
    {
        // Direct open attempt; the probe handle is consumed/closed either way.
        const int orc = interop_share_d3d12_to_d3d11(
            src, g_d3d11Device.Get(), view11.GetAddressOf());
        CloseHandle(probeHandle);
        probeHandle = nullptr;
        if (orc == CATRA_OK && view11)
        {
            shareableSrc = src;
        }
        else
        {
            BackendLog(CATRA_LOG_WARN,
                       "interop: encode workaround direct D3D11 open failed rc=%d — using staging copy",
                       orc);
        }
    }
    if (shareableSrc == nullptr)
    {
        const int src2 = EnsureEncodeStagingLocked(d12);
        if (src2 != CATRA_OK)
        {
            return src2;
        }
        const int crc = CopyD3D12ToStagingLocked(src, g_encodeStaging12.Get());
        if (crc != CATRA_OK)
        {
            return crc;
        }
        shareableSrc = g_encodeStaging12.Get();
        staged = true;

        hr = g_d3d12Device->CreateSharedHandle(
            shareableSrc,
            nullptr,
            GENERIC_ALL,
            nullptr,
            &probeHandle);
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR,
                       "interop: encode workaround staging CreateSharedHandle hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            return CATRA_ERR_DEVICE;
        }

        const int orc = interop_share_d3d12_to_d3d11(
            shareableSrc, g_d3d11Device.Get(), view11.GetAddressOf());
        CloseHandle(probeHandle);
        probeHandle = nullptr;
        if (orc != CATRA_OK || !view11)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "interop: encode workaround D3D12->D3D11 open failed rc=%d", orc);
            return CATRA_ERR_DEVICE;
        }
    }
    // Sync note: safe without a keyed-mutex acquire — the D3D12 producer
    // fence-waited before handing the texture out (backends fence-wait in
    // Process; the staging copy fence-waited above), so the shared allocation
    // is GPU-complete and this open view is its sole D3D11 consumer.

    D3D11_TEXTURE2D_DESC desc11;
    view11->GetDesc(&desc11);

    // --- Pooled GPU-GPU copy into the shared D3D11 pool (the AMF-accepted
    // slot resource comes out the other side).
    if (PooledCopyD3D11Locked(view11.Get(), desc11, out_pool_tex) == CATRA_OK)
    {
        *out_shared_handle = nullptr; // pool keeps its handles
        LogEncodeWorkaroundPath(staged);
        return CATRA_OK;
    }

    // --- CPU round-trip fallback (KNOWN ISSUE tolerance): on the current
    // RDNA 4 driver every keyed-mutex GPU-GPU operation returns
    // DXGI_ERROR_DEVICE_REMOVED (0x887A0001) — verified by bisect on the RX
    // 9070 XT; interop_share_d3d11_to_d3d12 tolerates it via this exact
    // pool12 round-trip, so the encode workaround does the same instead of
    // failing the export. The pool12 slot carries NO keyed mutex, and
    // AmfEncoder::Encode skips the ping-pong for mutex-less inputs (same as
    // today's working D3D11 path output). 32-bit BGRA/RGBA only: the upscale
    // contract (ReadBack11ToCpu assumes 4 bytes/pixel).
    if (desc11.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
        desc11.Format != DXGI_FORMAT_R8G8B8A8_UNORM)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: encode workaround CPU fallback unsupported fmt=%u",
                   static_cast<unsigned>(desc11.Format));
        return CATRA_ERR_DEVICE;
    }

    std::vector<uint8_t> cpu;
    UINT srcPitch = 0;
    if (!ReadBack11ToCpu(view11.Get(), desc11, cpu, srcPitch))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: encode workaround readback failed");
        return CATRA_ERR_DEVICE;
    }

    const int prc = EnsurePool12Locked(desc11);
    if (prc != CATRA_OK)
    {
        return prc;
    }

    Pool12Slot& slot = g_pool12[g_pool12Index];
    g_pool12Index = (g_pool12Index + 1) % g_pool12.size();
    if (!UploadCpuTo12(slot.tex.Get(), desc11, cpu, srcPitch, slot.state))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: encode workaround pool12 upload failed");
        return CATRA_ERR_DEVICE;
    }

    LogSharePath(false); // same one-time path log the D3D11 side emits
    LogEncodeWorkaroundPath(staged);
    *out_pool_tex = slot.tex.Get();
    slot.tex->AddRef();
    *out_shared_handle = nullptr; // no handle minted on the pool12 path
    return CATRA_OK;
}

// ===========================================================================
// Keyed mutex + copy primitives
// ===========================================================================

uint64_t interop_pool_generation()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_poolGeneration;
}

int interop_acquire(IDXGIKeyedMutex* mutex, uint64_t key, uint32_t timeout_ms)
{
    if (mutex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    HRESULT hr = mutex->AcquireSync(key, timeout_ms);
    // AMD driver quirk: AcquireSync may return raw Win32 WAIT_TIMEOUT (0x102)
    // with severity=0, so SUCCEEDED(hr) is true. Check timeout FIRST.
    if (IsAcquireTimeout(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: AcquireSync(key=%llu, %u ms) timed out hr=0x%08lX",
                   static_cast<unsigned long long>(key),
                   static_cast<unsigned>(timeout_ms),
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    if (SUCCEEDED(hr))
    {
        return CATRA_OK;
    }
    return HrToCatra(hr);
}

int interop_release(IDXGIKeyedMutex* mutex, uint64_t key)
{
    if (mutex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    return HrToCatra(mutex->ReleaseSync(key));
}

int interop_copy_d3d11(ID3D11DeviceContext* ctx,
                       ID3D11Texture2D* src,
                       ID3D11Texture2D* dst_shared)
{
    if (ctx == nullptr || src == nullptr || dst_shared == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }

    // CopyResource requires identical geometry; validate up front so a
    // mismatch is a clean CATRA_ERR_INVALID_ARG instead of a device removal.
    D3D11_TEXTURE2D_DESC srcDesc;
    D3D11_TEXTURE2D_DESC dstDesc;
    src->GetDesc(&srcDesc);
    dst_shared->GetDesc(&dstDesc);
    if (!SameGeometry(srcDesc, dstDesc))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interop: copy geometry mismatch %ux%u fmt=%u -> %ux%u fmt=%u",
                   srcDesc.Width, srcDesc.Height,
                   static_cast<unsigned>(srcDesc.Format),
                   dstDesc.Width, dstDesc.Height,
                   static_cast<unsigned>(dstDesc.Format));
        return CATRA_ERR_INVALID_ARG;
    }

    ctx->CopyResource(dst_shared, src); // GPU-GPU; completes on the D3D11 timeline
    return CATRA_OK;
}

// ===========================================================================
// ST-13/14 compatibility wrappers (HRESULT surface, signatures frozen)
// ===========================================================================

HRESULT CreateD3D12Device(ID3D11Device* d3d11Device, ID3D12Device** out)
{
    if (out == nullptr)
    {
        return E_INVALIDARG;
    }
    *out = nullptr;

    if (d3d11Device == nullptr)
    {
        return E_INVALIDARG;
    }

    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_d3d12Device)
        {
            // Interop is up: hand out the shared bridge device so every
            // backend dispatches on the same D3D12 device (one adapter, one
            // LUID — the precondition for shared-handle interop).
            *out = g_d3d12Device.Get();
            g_d3d12Device->AddRef();
            return S_OK;
        }
    }

    // interop_init has not run (standalone use / catra_init soft-failed):
    // create a private device on the same adapter.
    return CreateD3D12DeviceOnAdapter(d3d11Device, out);
}

HRESULT ShareTexture(ID3D11Device* d3d11Device,
                     ID3D12Device* d3d12Device,
                     ID3D11Texture2D* source,
                     ID3D12Resource** out)
{
    if (out == nullptr)
    {
        return E_INVALIDARG;
    }
    *out = nullptr;

    if (d3d11Device == nullptr || d3d12Device == nullptr || source == nullptr)
    {
        return E_INVALIDARG;
    }

    bool pooled = false;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        pooled = g_d3d12Device && g_d3d12Device.Get() == d3d12Device;
    }

    if (pooled)
    {
        // The caller's D3D12 device IS the interop device (the normal case
        // after catra_init): route through the pooled share so frames reuse
        // persistent shared textures instead of allocating per call.
        ID3D12Resource* resource = nullptr;
        int rc = interop_share_d3d11_to_d3d12(source, &resource, nullptr);
        if (rc != CATRA_OK)
        {
            return CatraToHResult(rc);
        }
        *out = resource;
        return S_OK;
    }

    // Degraded path (interop not initialized, or a foreign D3D12 device):
    // share directly on the caller-provided devices, without the pool.
    D3D11_TEXTURE2D_DESC desc;
    source->GetDesc(&desc);
    const bool ntShared =
        (desc.MiscFlags & D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX) != 0 &&
        (desc.MiscFlags & D3D11_RESOURCE_MISC_SHARED_NTHANDLE) != 0;
    if (ntShared)
    {
        return ShareD3D11ToD3D12Direct(d3d12Device, source, out, nullptr);
    }
    return ShareViaTempCopy(d3d11Device, d3d12Device, source, out);
}

} // namespace catra

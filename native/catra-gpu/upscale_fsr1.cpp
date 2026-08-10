// upscale_fsr1.cpp — FSR 1 (EASU) spatial upscale backend (ST-14).
//
// See upscale_fsr1.h for the design overview. The EASU compute shader is
// embedded as HLSL below and compiled to DXBC at runtime (D3DCompile), so the
// backend is fully self-contained: no FidelityFX SDK, no dxc, no on-disk shader
// blob. The DX12 dispatch path rides on the D3D11<->DX12 interop (ST-15, now
// implemented in d3d_interop.cpp): catra::CreateD3D12Device returns the shared
// bridge device and catra::ShareTexture hands back a D3D12 resource backed by
// the pooled GPU-GPU copy (or a zero-copy NT share). Because that resource is
// co-owned with the interop pool, Process brackets its dispatch with the
// consumer half of the keyed-mutex ping-pong (see d3d_interop.h) so the read
// never races the pool's next copy on real hardware.
//
// ROOT-CAUSE / DESIGN NOTES
//   * EASU is a single fixed spatial algorithm — FSR 1 has no quality presets.
//     The UpscaleQualityMode selected at create time is recorded for parity
//     with the FSR 4 path and for diagnostics only.
//   * Runtime shader compile (DXBC) is chosen over a precompiled DXIL blob so
//     nothing binary has to be committed; the DX12 runtime translates DXBC to
//     DXIL internally. On a shipping build the compile is a one-time cost at
//     catra_upscale_create (microseconds), amortized over the whole job.
//   * Every HRESULT failure is mapped to a CATRA_ERR_* code and logged; no
//     exception is thrown out of this TU (the C ABI guard in catra_gpu.cpp is
//     the last line of defence, but we never rely on it for control flow).

#include "upscale_fsr1.h"
#include "d3d_interop.h"
#include "catra_gpu.h"

#include <cmath>
#include <cstring>

#if defined(_WIN32)
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
#endif

#include <d3d11.h>
#include <d3d12.h>
#include <d3dcompiler.h>
// d3dx12.h helpers removed: the vcpkg directx-headers version (1.619+) requires
// a newer SDK than 10.0.26100. Root signature is built with raw D3D12 structs.
#include <dxgi1_4.h>
#include <wrl/client.h>

// BackendLog lives in catra_gpu.cpp; redeclare the shim so this TU can emit
// through the managed log sink without duplicating the plumbing.
namespace catra {
void BackendLog(int level, const char* fmt, ...);
}

namespace catra {

namespace {

using Microsoft::WRL::ComPtr;

// Maps an HRESULT to a CATRA_ERR_* code. E_NOTIMPL is surfaced distinctly so
// the caller can tell a genuine "not implemented" from a device fault.
int HrToCatra(HRESULT hr)
{
    if (SUCCEEDED(hr))
    {
        return CATRA_OK;
    }
    if (hr == E_NOTIMPL)
    {
        return CATRA_ERR_NOT_IMPL;
    }
    if (hr == E_INVALIDARG)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    return CATRA_ERR_DEVICE;
}

// RAII guard for the CONSUMER half of the keyed-mutex ping-pong (ST-15). The
// shared input resource is co-owned with the interop pool, whose producer does
// Acquire(k)->copy->Release(k) with k alternating 0/1 (g_frameKey). The
// consumer must Acquire(k) before reading and Release(k) after, on EVERY exit
// path, or a stuck key deadlocks the producer two frames later. This guard
// releases (and advances the consumer key) in its destructor; it is a no-op
// when the resource carried no keyed mutex (mutex == null / not held), which is
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

// ---------------------------------------------------------------------------
// EASU compute shader (FSR 1, self-contained HLSL)
// ---------------------------------------------------------------------------
// One thread per OUTPUT pixel ([numthreads(16,16,1)]). For each output pixel the
// shader:
//   1. maps the output-pixel centre back into input space,
//   2. gathers the 4x4 input neighbourhood around that point (point loads),
//   3. computes a separable 4-tap Lanczos-2 resample,
//   4. derives the local edge direction from the luminance gradient and clamps
//      the result to a directional min/max so edges stay crisp without ringing
//      (the "edge adaptive" part of EASU).
// Constants arrive in a root constant buffer (b0): source + display sizes.
const char* const kEasuHlsl = R"HLSL(
#define CATRA_PI 3.14159265358979323846

Texture2D<float4>   InputTex  : register(t0);
RWTexture2D<float4> OutputTex : register(u0);

cbuffer EasuConst : register(b0)
{
    float2 SrcSize;    // input  width, height
    float2 DstSize;    // output width, height
};

// Lanczos-2 kernel: sinc(x) * sinc(x/2), |x| < 2.
float Lanczos2(float x)
{
    x = abs(x);
    if (x < 1e-6)
    {
        return 1.0;
    }
    if (x >= 2.0)
    {
        return 0.0;
    }
    float px  = CATRA_PI * x;
    float px2 = CATRA_PI * x * 0.5;
    return (sin(px) / px) * (sin(px2) / px2);
}

float Luma(float3 c)
{
    // Rec.709 luma.
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// Point-loads an input texel, clamping to the valid range (EASU taps can reach
// one pixel past the border for output pixels near the frame edge).
float3 Tap(int2 p)
{
    p = clamp(p, int2(0, 0), int2((int)SrcSize.x - 1, (int)SrcSize.y - 1));
    return InputTex.Load(int3(p, 0)).rgb;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 gid : SV_DispatchThreadID)
{
    if (gid.x >= (uint)DstSize.x || gid.y >= (uint)DstSize.y)
    {
        return;
    }

    // 1. Output-pixel centre -> input space (pixel centres at integer coords).
    float2 scale  = SrcSize / DstSize;
    float2 inPos  = ((float2)gid.xy + 0.5) * scale - 0.5;
    int2   base   = (int2)floor(inPos);   // top-left of the 2x2 cell
    float2 f      = inPos - (float2)base; // fraction in [0,1)

    // 2. Gather the 4x4 neighbourhood (offsets -1..+2 around 'base').
    float3 t[4][4];
    [unroll]
    for (int j = 0; j < 4; ++j)
    {
        [unroll]
        for (int i = 0; i < 4; ++i)
        {
            t[j][i] = Tap(base + int2(i - 1, j - 1));
        }
    }

    // 3. Separable Lanczos-2 resample. Weights from the fractional position.
    float wx[4];
    float wy[4];
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        wx[i] = Lanczos2((float)(i - 1) - f.x);
        wy[i] = Lanczos2((float)(i - 1) - f.y);
    }

    float3 color = float3(0, 0, 0);
    float  wsum  = 0.0;
    [unroll]
    for (int j = 0; j < 4; ++j)
    {
        float3 row = float3(0, 0, 0);
        float  rw  = 0.0;
        [unroll]
        for (int i = 0; i < 4; ++i)
        {
            row += t[j][i] * wx[i];
            rw  += wx[i];
        }
        color += row * wy[j];
        wsum  += rw * wy[j];
    }
    color /= max(wsum, 1e-6);

    // 4. Edge-adaptive clamp. The 4 nearest taps (the 2x2 'base' cell) bound the
    //    plausible value; a directional min/max (min/max along the dominant
    //    gradient axis, widened by the cross axis) prevents Lanczos overshoot on
    //    high-contrast edges while leaving flat areas untouched.
    float3 c00 = t[1][1];
    float3 c10 = t[1][2];
    float3 c01 = t[2][1];
    float3 c11 = t[2][2];

    float3 mn = min(min(c00, c10), min(c01, c11));
    float3 mx = max(max(c00, c10), max(c01, c11));

    // Dominant edge direction from the luma gradient of the 2x2 cell.
    float gx = Luma(c10 + c11) - Luma(c00 + c01);
    float gy = Luma(c01 + c11) - Luma(c00 + c10);
    // Widen the clamp a little along the edge tangent so genuine texture is not
    // flattened, but keep it tight across the edge to kill ringing.
    float edge = abs(gx) + abs(gy);
    float loosen = 0.25 * edge;
    mn -= loosen;
    mx += loosen;

    color = clamp(color, mn, mx);

    OutputTex[int2(gid.xy)] = float4(color, 1.0);
}
)HLSL";

} // namespace

// ---------------------------------------------------------------------------
// Fsr1Upscaler::Impl — DX12 compute pipeline state
// ---------------------------------------------------------------------------

struct Fsr1Upscaler::Impl
{
    ID3D11Device* d3d11Device = nullptr; // borrowed (bridge-owned), for ShareTexture
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> cmdList;
    ComPtr<ID3D12Fence> fence;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;

    ComPtr<ID3D12RootSignature> rootSignature;
    ComPtr<ID3D12PipelineState> pipelineState;
    ComPtr<ID3D12DescriptorHeap> srvHeap; // shader-visible: SRV(t0) + UAV(u0)
    ComPtr<ID3D12Resource> constBuffer;   // root constant buffer (b0)

    UINT cbvSrvUavSize = 0;

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

Fsr1Upscaler::~Fsr1Upscaler() = default;

int Fsr1Upscaler::Create(ID3D11Device* d3d11Device,
                         int srcW, int srcH, int dstW, int dstH,
                         std::unique_ptr<Fsr1Upscaler>& out)
{
    out.reset();

    if (d3d11Device == nullptr || srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: invalid create args (%dx%d -> %dx%d)",
                   srcW, srcH, dstW, dstH);
        return CATRA_ERR_INVALID_ARG;
    }

    auto self = std::unique_ptr<Fsr1Upscaler>(new Fsr1Upscaler());
    self->m_srcW = srcW;
    self->m_srcH = srcH;
    self->m_dstW = dstW;
    self->m_dstH = dstH;
    self->m_impl = std::make_unique<Impl>();
    Impl& d = *self->m_impl;
    d.d3d11Device = d3d11Device; // borrowed; outlives the context (shutdown ordering)

    // --- D3D12 device on the bridge's adapter (ST-15 interop) --------------
    // CreateD3D12Device returns the shared interop device when interop_init has
    // run (the normal case), else a standalone device on the same adapter.
    HRESULT hr = CreateD3D12Device(d3d11Device, d.device.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr1: CreateD3D12Device hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    // --- Command queue / allocator / list / fence --------------------------
    D3D12_COMMAND_QUEUE_DESC queueDesc = {};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    queueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
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
        // Start closed; Process resets it per dispatch.
        d.cmdList->Close();
        hr = d.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(d.fence.GetAddressOf()));
    }
    if (SUCCEEDED(hr))
    {
        d.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (d.fenceEvent == nullptr)
        {
            hr = HRESULT_FROM_WIN32(GetLastError());
        }
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: command objects hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    // --- Compile the EASU shader (DXBC; runtime translates to DXIL) --------
    ComPtr<ID3DBlob> shaderBlob;
    ComPtr<ID3DBlob> errorBlob;
    hr = D3DCompile(kEasuHlsl, std::strlen(kEasuHlsl), "catra_fsr1_easu",
                    nullptr, nullptr, "CSMain", "cs_5_1",
                    D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                    shaderBlob.GetAddressOf(), errorBlob.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: EASU compile hr=0x%08lX%s%s",
                   static_cast<unsigned long>(hr),
                   errorBlob ? ": " : "",
                   errorBlob ? static_cast<const char*>(errorBlob->GetBufferPointer()) : "");
        return CATRA_ERR_DEVICE;
    }

    // --- Root signature: b0 (constants) + t0 (SRV) + u0 (UAV) --------------
    // Built with raw D3D12 structs (no d3dx12.h dependency).
    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    ranges[0].NumDescriptors = 1;
    ranges[0].BaseShaderRegister = 0; // t0
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    ranges[1].NumDescriptors = 1;
    ranges[1].BaseShaderRegister = 0; // u0

    D3D12_ROOT_PARAMETER params[2] = {};
    params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
    params[0].Descriptor.ShaderRegister = 0; // b0
    params[0].Descriptor.RegisterSpace = 0;
    params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[1].DescriptorTable.NumDescriptorRanges = 2;
    params[1].DescriptorTable.pDescriptorRanges = ranges;
    params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

    D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
    rsDesc.NumParameters = 2;
    rsDesc.pParameters = params;
    rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

    ComPtr<ID3DBlob> rsBlob;
    ComPtr<ID3DBlob> rsError;
    hr = D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1,
                                     rsBlob.GetAddressOf(), rsError.GetAddressOf());
    if (SUCCEEDED(hr))
    {
        hr = d.device->CreateRootSignature(
            0, rsBlob->GetBufferPointer(), rsBlob->GetBufferSize(),
            IID_PPV_ARGS(d.rootSignature.GetAddressOf()));
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: root signature hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Compute pipeline state --------------------------------------------
    D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
    psoDesc.pRootSignature = d.rootSignature.Get();
    psoDesc.CS.pShaderBytecode = shaderBlob->GetBufferPointer();
    psoDesc.CS.BytecodeLength = shaderBlob->GetBufferSize();
    hr = d.device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(d.pipelineState.GetAddressOf()));
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: PSO hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Shader-visible descriptor heap (SRV + UAV) ------------------------
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    heapDesc.NumDescriptors = 2;
    heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    hr = d.device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(d.srvHeap.GetAddressOf()));
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: descriptor heap hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    d.cbvSrvUavSize = d.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

    // --- Uploaded constant buffer (source/display sizes) -------------------
    D3D12_RESOURCE_DESC cbDesc = {};
    cbDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    cbDesc.Width = 16; // 2 x float2 (SrcSize, DstSize)
    cbDesc.Height = 1;
    cbDesc.DepthOrArraySize = 1;
    cbDesc.MipLevels = 1;
    cbDesc.Format = DXGI_FORMAT_UNKNOWN;
    cbDesc.SampleDesc.Count = 1;
    cbDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    D3D12_HEAP_PROPERTIES uploadHeap = {};
    uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;
    hr = d.device->CreateCommittedResource(
        &uploadHeap, D3D12_HEAP_FLAG_NONE, &cbDesc,
        D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
        IID_PPV_ARGS(d.constBuffer.GetAddressOf()));
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: const buffer hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    float constants[4] = {
        static_cast<float>(srcW), static_cast<float>(srcH),
        static_cast<float>(dstW), static_cast<float>(dstH)};
    void* mapped = nullptr;
    D3D12_RANGE readRange = {0, 0};
    hr = d.constBuffer->Map(0, &readRange, &mapped);
    if (SUCCEEDED(hr))
    {
        std::memcpy(mapped, constants, sizeof(constants));
        d.constBuffer->Unmap(0, nullptr);
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: const map hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    BackendLog(CATRA_LOG_INFO,
               "upscale_fsr1: EASU pipeline ready %dx%d -> %dx%d", srcW, srcH, dstW, dstH);

    out = std::move(self);
    return CATRA_OK;
}

int Fsr1Upscaler::Process(ID3D11Texture2D* src, ID3D12Resource** outDst)
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

    // --- D3D11 -> DX12 shared input resource (ST-15 interop) ---------------
    // ShareTexture bridges the bridge's D3D11 device + source texture into a
    // DX12 resource on the device we own, via the pooled GPU-GPU copy (or a
    // zero-copy NT share). The returned resource is co-owned with the interop
    // pool, so it carries a keyed mutex we must honor around the dispatch.
    ComPtr<ID3D12Resource> srcRes;
    HRESULT hr = ShareTexture(d.d3d11Device, d.device.Get(), src, srcRes.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr1: ShareTexture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    // --- Keyed-mutex consumer acquire (ST-15 ping-pong) --------------------
    // QI the keyed mutex off the shared resource; shared resources expose it on
    // both API sides. A resource we own outright (no shared heap) yields S_FALSE
    // and a null mutex -> we skip the acquire (guard stays inert). AcquireSync
    // blocks until the pool producer's matching Release(key) completes on the
    // GPU timeline, which both synchronizes the cross-queue hand-off and provides
    // backpressure. A timeout/failure is a device fault (CATRA_ERR_DEVICE).
    KeyedMutexGuard mutexGuard;
    mutexGuard.nextKey = &d.consumerKey;
    srcRes->QueryInterface(IID_PPV_ARGS(&mutexGuard.mutex)); // S_FALSE when absent -> mutex stays null
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
        int arc = interop_acquire(mutexGuard.mutex, mutexGuard.key, 5000);
        if (arc != CATRA_OK)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "upscale_fsr1: keyed-mutex acquire rc=%d (key=%llu)",
                       arc, static_cast<unsigned long long>(mutexGuard.key));
            return CATRA_ERR_DEVICE;
        }
        mutexGuard.held = true; // destructor releases on every exit from here
    }

    // --- Output UAV texture (dstW x dstH, RGBA8) ---------------------------
    D3D12_RESOURCE_DESC outDesc = {};
    outDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    outDesc.Width = static_cast<UINT>(m_dstW);
    outDesc.Height = static_cast<UINT>(m_dstH);
    outDesc.DepthOrArraySize = 1;
    outDesc.MipLevels = 1;
    outDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; // BGRA for AMF encoder
    outDesc.SampleDesc.Count = 1;
    outDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    outDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS; // BGRA for AMF encoder
    D3D12_HEAP_PROPERTIES defaultHeap = {};
    defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;

    ComPtr<ID3D12Resource> dstRes;
    hr = d.device->CreateCommittedResource(
        &defaultHeap, D3D12_HEAP_FLAG_SHARED, &outDesc,
        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
        IID_PPV_ARGS(dstRes.GetAddressOf()));
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: output texture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Bind SRV (input) + UAV (output) into the shader-visible heap ------
    D3D12_CPU_DESCRIPTOR_HANDLE heapCpu = d.srvHeap->GetCPUDescriptorHandleForHeapStart();
    D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    srvDesc.Texture2D.MipLevels = 1;
    d.device->CreateShaderResourceView(srcRes.Get(), &srvDesc, heapCpu);

    D3D12_CPU_DESCRIPTOR_HANDLE uavCpu = heapCpu;
    uavCpu.ptr += d.cbvSrvUavSize;
    D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
    uavDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; // BGRA for AMF encoder
    uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    d.device->CreateUnorderedAccessView(dstRes.Get(), nullptr, &uavDesc, uavCpu);

    // --- Record + dispatch --------------------------------------------------
    hr = d.allocator->Reset();
    if (SUCCEEDED(hr))
    {
        hr = d.cmdList->Reset(d.allocator.Get(), d.pipelineState.Get());
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: cmd reset hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    d.cmdList->SetComputeRootSignature(d.rootSignature.Get());
    ID3D12DescriptorHeap* heaps[] = {d.srvHeap.Get()};
    d.cmdList->SetDescriptorHeaps(1, heaps);
    d.cmdList->SetComputeRootConstantBufferView(0, d.constBuffer->GetGPUVirtualAddress());
    d.cmdList->SetComputeRootDescriptorTable(1, d.srvHeap->GetGPUDescriptorHandleForHeapStart());

    const UINT groupsX = (static_cast<UINT>(m_dstW) + 15) / 16;
    const UINT groupsY = (static_cast<UINT>(m_dstH) + 15) / 16;
    d.cmdList->Dispatch(groupsX, groupsY, 1);
    d.cmdList->Close();

    ID3D12CommandList* lists[] = {d.cmdList.Get()};
    d.queue->ExecuteCommandLists(1, lists);

    // --- Wait for completion (offline pipeline: synchronous is fine) -------
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
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: fence wait hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Transition texture to COMMON state for AMF consumption ------------
    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = dstRes.Get();
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    
    hr = d.allocator->Reset();
    if (SUCCEEDED(hr))
    {
        hr = d.cmdList->Reset(d.allocator.Get(), nullptr);
    }
    if (SUCCEEDED(hr))
    {
        d.cmdList->ResourceBarrier(1, &barrier);
        d.cmdList->Close();
        ID3D12CommandList* lists2[] = {d.cmdList.Get()};
        d.queue->ExecuteCommandLists(1, lists2);
        
        // Wait for transition to complete
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
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: transition barrier hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    *outDst = dstRes.Detach(); // caller owns the reference
    return CATRA_OK;
}

// ---------------------------------------------------------------------------
// Pure decision helpers (GPU-free; pinned by tests/native/test_upscale.cpp)
// ---------------------------------------------------------------------------

UpscaleQualityMode SelectQualityMode(int srcW, int srcH, int dstW, int dstH)
{
    // Guard degenerate input: no upscale (or downscale) => Quality is a harmless
    // default; the caller is expected to request passthrough (RN-07) anyway.
    if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
    {
        return UpscaleQualityMode::Quality;
    }

    // Linear upscale ratio on the height axis (the "p" resolution convention).
    const float scale = static_cast<float>(dstH) / static_cast<float>(srcH);

    // Offline video pipeline => quality bias (see header for the full rationale
    // and the three spec anchors this satisfies exactly).
    if (scale >= 3.0f)
    {
        return UpscaleQualityMode::UltraPerformance; // 720p -> 4K
    }
    return UpscaleQualityMode::Quality; // 1080p -> 4K, 720p -> 1080p, ...
}

int ResolveUpscaleMethod(int requested, bool fsr4Available)
{
    switch (requested)
    {
        case CATRA_UPSCALE_OFF:
            return CATRA_UPSCALE_OFF; // passthrough, always honoured
        case CATRA_UPSCALE_FSR1:
            return CATRA_UPSCALE_FSR1; // always available
        case CATRA_UPSCALE_FSR4:
            // Graceful downgrade when FSR 4 cannot run (no SDK / not RDNA 4).
            return fsr4Available ? CATRA_UPSCALE_FSR4 : CATRA_UPSCALE_FSR1;
        default:
            // Defensive: an unknown method degrades to passthrough rather than
            // risking an undefined backend dispatch.
            return CATRA_UPSCALE_OFF;
    }
}

} // namespace catra

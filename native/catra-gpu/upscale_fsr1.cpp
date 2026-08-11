// upscale_fsr1.cpp — FSR 1 (EASU) spatial upscale backend (ST-14).
//
// IMPLEMENTATION NOTE (AMD RDNA 4 black-frame root cause, fixed here):
//   The original backend ran EASU as a D3D12 compute dispatch whose descriptor
//   table combined an SRV (input) + UAV (output). On the AMD RDNA 4 driver
//   (RX 9070 XT / Adrenalin 25.x) a D3D12 compute dispatch that has an SRV in
//   its bound descriptor table is silently DROPPED (or TDRs): the UAV receives
//   no writes at all (verified with GPU readback probes — even a sentinel
//   write that never touches the SRV produced alpha=0), while an otherwise
//   identical UAV-only dispatch works. D3D12 validation reports nothing.
//
//   The fix moves EASU to a D3D11 compute dispatch (SRV+UAV on D3D11 is proven
//   reliable on this driver — the NV12->BGRA backend uses the same pattern),
//   producing a D3D11 BGRA texture that is then bridged to D3D12 for the AMF
//   encoder via the interop CPU round-trip (the one share path verified to
//   carry real pixels on this machine). No D3D12 compute, no D3D12 SRV.
//
// The EASU algorithm itself is unchanged: single fixed spatial algorithm, no
// quality presets. The UpscaleQualityMode selected at create time is recorded
// for parity with the FSR 4 path and for diagnostics only.

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

// Maps an HRESULT to a CATRA_ERR_* code.
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

// ---------------------------------------------------------------------------
// EASU compute shader (FSR 1, self-contained HLSL) — D3D11 compute (cs_5_0).
// ---------------------------------------------------------------------------
// One thread per OUTPUT pixel ([numthreads(16,16,1)]). For each output pixel:
//   1. map the output-pixel centre back into input space,
//   2. gather the 4x4 input neighbourhood around that point (point loads),
//   3. compute a separable 4-tap Lanczos-2 resample,
//   4. derive the local edge direction from the luminance gradient and clamp
//      the result to a directional min/max (the "edge adaptive" part of EASU).
// Input SRV (t0) and output UAV (u0) are both BGRA; constants via cbuffer b0.
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
    //    plausible value; a directional min/max prevents Lanczos overshoot on
    //    high-contrast edges while leaving flat areas untouched.
    float3 c00 = t[1][1];
    float3 c10 = t[1][2];
    float3 c01 = t[2][1];
    float3 c11 = t[2][2];

    float3 mn = min(min(c00, c10), min(c01, c11));
    float3 mx = max(max(c00, c10), max(c01, c11));

    float gx = Luma(c10 + c11) - Luma(c00 + c01);
    float gy = Luma(c01 + c11) - Luma(c00 + c10);
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
// Fsr1Upscaler::Impl — D3D11 compute pipeline state
// ---------------------------------------------------------------------------

struct Fsr1Upscaler::Impl
{
    ID3D11Device* d3d11Device = nullptr;      // borrowed (bridge-owned)
    ComPtr<ID3D11DeviceContext> ctx;          // immediate context for dispatch
    ComPtr<ID3D12Device> device;              // for the final D3D11->D3D12 share

    ComPtr<ID3D11ComputeShader> computeShader;
    ComPtr<ID3D11Buffer> constBuffer;         // cbuffer b0 (SrcSize, DstSize)

    // Cached per-size output (UAV) texture + view.
    ComPtr<ID3D11Texture2D> outTex;
    ComPtr<ID3D11UnorderedAccessView> outUav;
    int outW = 0;
    int outH = 0;
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
    d.d3d11Device = d3d11Device; // borrowed; outlives the context

    d3d11Device->GetImmediateContext(d.ctx.GetAddressOf());
    if (d.ctx == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: no immediate context");
        return CATRA_ERR_DEVICE;
    }

    // D3D12 device on the bridge's adapter, used only to hand the upscaled
    // D3D11 texture to the AMF encoder via the interop share (CPU round-trip).
    HRESULT hr = CreateD3D12Device(d3d11Device, d.device.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "upscale_fsr1: CreateD3D12Device hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }

    // --- Compile the EASU shader for D3D11 compute (cs_5_0) ----------------
    ComPtr<ID3DBlob> shaderBlob;
    ComPtr<ID3DBlob> errorBlob;
    hr = D3DCompile(kEasuHlsl, std::strlen(kEasuHlsl), "catra_fsr1_easu",
                    nullptr, nullptr, "CSMain", "cs_5_0",
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

    hr = d3d11Device->CreateComputeShader(shaderBlob->GetBufferPointer(),
                                          shaderBlob->GetBufferSize(),
                                          nullptr, d.computeShader.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: CreateComputeShader hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Dynamic constant buffer (SrcSize, DstSize) ------------------------
    D3D11_BUFFER_DESC cbDesc = {};
    cbDesc.ByteWidth = 16; // 2 x float2
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = d3d11Device->CreateBuffer(&cbDesc, nullptr, d.constBuffer.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: const buffer hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    BackendLog(CATRA_LOG_INFO,
               "upscale_fsr1: EASU (D3D11 compute) pipeline ready %dx%d -> %dx%d",
               srcW, srcH, dstW, dstH);

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

    D3D11_TEXTURE2D_DESC srcDesc;
    src->GetDesc(&srcDesc);
    if ((srcDesc.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0)
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: src not shader-bindable (bind=0x%02X)",
                   static_cast<unsigned>(srcDesc.BindFlags));
        return CATRA_ERR_DEVICE;
    }

    // --- Ensure the BGRA output (UAV) texture matches the target size ------
    if (d.outTex == nullptr || d.outW != m_dstW || d.outH != m_dstH)
    {
        d.outUav.Reset();
        d.outTex.Reset();

        D3D11_TEXTURE2D_DESC od = {};
        od.Width = static_cast<UINT>(m_dstW);
        od.Height = static_cast<UINT>(m_dstH);
        od.MipLevels = 1;
        od.ArraySize = 1;
        od.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        od.SampleDesc.Count = 1;
        od.Usage = D3D11_USAGE_DEFAULT;
        od.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr = d.d3d11Device->CreateTexture2D(&od, nullptr, d.outTex.GetAddressOf());
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: output tex hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            return CATRA_ERR_DEVICE;
        }
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
        ud.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
        hr = d.d3d11Device->CreateUnorderedAccessView(d.outTex.Get(), &ud,
                                                      d.outUav.GetAddressOf());
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: output UAV hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            d.outTex.Reset();
            return CATRA_ERR_DEVICE;
        }
        d.outW = m_dstW;
        d.outH = m_dstH;
    }

    // --- SRV on the source --------------------------------------------------
    ComPtr<ID3D11ShaderResourceView> srv;
    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.Format = srcDesc.Format;
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sd.Texture2D.MipLevels = 1;
    HRESULT hr = d.d3d11Device->CreateShaderResourceView(src, &sd, srv.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "upscale_fsr1: src SRV hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Update constants ---------------------------------------------------
    D3D11_MAPPED_SUBRESOURCE map = {};
    hr = d.ctx->Map(d.constBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &map);
    if (SUCCEEDED(hr))
    {
        float constants[4] = {
            static_cast<float>(m_srcW), static_cast<float>(m_srcH),
            static_cast<float>(m_dstW), static_cast<float>(m_dstH)};
        std::memcpy(map.pData, constants, sizeof(constants));
        d.ctx->Unmap(d.constBuffer.Get(), 0);
    }

    // --- Bind + dispatch (D3D11 compute) ------------------------------------
    ID3D11ShaderResourceView* srvs[1] = {srv.Get()};
    ID3D11UnorderedAccessView* uavs[1] = {d.outUav.Get()};
    ID3D11Buffer* cbs[1] = {d.constBuffer.Get()};

    d.ctx->CSSetShader(d.computeShader.Get(), nullptr, 0);
    d.ctx->CSSetShaderResources(0, 1, srvs);
    d.ctx->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
    d.ctx->CSSetConstantBuffers(0, 1, cbs);

    const UINT groupsX = (static_cast<UINT>(m_dstW) + 15) / 16;
    const UINT groupsY = (static_cast<UINT>(m_dstH) + 15) / 16;
    d.ctx->Dispatch(groupsX, groupsY, 1);

    // Unbind to avoid dangling references on the context.
    ID3D11ShaderResourceView* nullSrvs[1] = {nullptr};
    ID3D11UnorderedAccessView* nullUavs[1] = {nullptr};
    ID3D11Buffer* nullCbs[1] = {nullptr};
    d.ctx->CSSetShaderResources(0, 1, nullSrvs);
    d.ctx->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
    d.ctx->CSSetConstantBuffers(0, 1, nullCbs);
    d.ctx->CSSetShader(nullptr, nullptr, 0);

    // --- Bridge the D3D11 result to D3D12 for the AMF encoder ---------------
    // The interop share uses the CPU round-trip on this driver, which both
    // submits+waits for the D3D11 work and is the verified-good share path.
    hr = ShareTexture(d.d3d11Device, d.device.Get(), d.outTex.Get(), outDst);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN, "upscale_fsr1: ShareTexture hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return HrToCatra(hr);
    }
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

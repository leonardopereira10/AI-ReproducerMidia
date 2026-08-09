// nv12_to_bgra_shader.cpp — GPU compute shader NV12 → BGRA conversion (ST-23).
//
// See nv12_to_bgra_shader.h for the design overview. The HLSL compute shader
// is embedded below and compiled to DXBC at runtime via D3DCompile, matching
// the pattern from upscale_fsr1.cpp. The shader reads Y (R8) and UV (R8G8)
// from two SRVs bound to a single NV12 texture array slice and writes BGRA
// (R8G8B8A8) to a UAV. BT.601 full-range conversion.
//
// WHY D3D11 (not DX12): the decoder produces D3D11VA textures and the
// pipeline's first GPU consumer (RIFE interp) also runs on D3D11. Running the
// conversion on the same device/context avoids a cross-API share for what is
// a trivially small compute dispatch (one thread group per 16×16 tile).

#include "nv12_to_bgra_shader.h"
#include "catra_gpu.h"

#include <atomic>
#include <cstring>

#if defined(_WIN32)
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
#endif

#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

// BackendLog lives in catra_gpu.cpp; redeclare the shim so this TU can emit
// through the managed log sink without duplicating the plumbing.
namespace catra {
void BackendLog(int level, const char* fmt, ...);
}

namespace catra {

namespace {

using Microsoft::WRL::ComPtr;

// ---------------------------------------------------------------------------
// Embedded HLSL — NV12 (R8 Y + R8G8 UV) → BGRA, BT.601 full-range
// ---------------------------------------------------------------------------
// The shader is compiled as cs_5_0 (D3D11 compute shader model 5.0) so it
// works on any Feature Level 11.0+ adapter (every GPU that can run D3D11VA
// decode). numthreads(16,16,1) matches the FSR 1 dispatch group size.
constexpr const char* kNv12ToBgraHlsl = R"HLSL(
// NV12 (R8 Y plane + R8G8 interleaved UV plane) -> BGRA R8G8B8A8.
// BT.601 full-range: Y in [0,1], UV in [-0.5, 0.5].

RWTexture2D<float4> output : register(u0);
Texture2D<float>    yTex   : register(t0);
Texture2D<float2>   uvTex  : register(t1);

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID)
{
    // Luma (full resolution).
    float Y = yTex.Load(uint3(dtid.xy, 0)).r;

    // Chroma (half resolution in each axis — UV plane is width/2 x height/2).
    float2 UV = uvTex.Load(uint3(dtid.xy / 2, 0)).rg;
    float U = UV.x - 0.5;
    float V = UV.y - 0.5;

    // BT.601 full-range -> RGB.
    float R = Y + 1.402 * V;
    float G = Y - 0.344136 * U - 0.714136 * V;
    float B = Y + 1.772 * U;

    // Clamp to [0,1] and write as BGRA (the texture format is R8G8B8A8, and
    // the pipeline downstream expects BGRA channel order in memory, which
    // means R=B, G=G, B=R, A=1 in the float4 write).
    output[dtid.xy] = float4(
        saturate(B),
        saturate(G),
        saturate(R),
        1.0);
}
)HLSL";

// ---------------------------------------------------------------------------
// Module-level cached state
// ---------------------------------------------------------------------------
// The compiled shader is device-independent in principle, but the COM pointer
// is tied to the device that created it. Since the bridge uses a single D3D11
// device for its entire lifetime, caching on the module is safe.

std::atomic<bool> g_ready{false};
ComPtr<ID3D11ComputeShader> g_computeShader;

// Cached BGRA output texture + UAV. Re-created when the frame size changes
// (different video source). Guarded by the caller's serial decode contract.
unsigned int g_cachedWidth = 0;
unsigned int g_cachedHeight = 0;
ComPtr<ID3D11Texture2D> g_cachedBgraTex;
ComPtr<ID3D11UnorderedAccessView> g_cachedUav;

// Ensures the cached BGRA output texture matches the requested dimensions.
// Re-creates if the size changed (e.g., switching video sources).
int EnsureBgraOutput(ID3D11Device* device, unsigned int width, unsigned int height)
{
    if (g_cachedBgraTex && g_cachedWidth == width && g_cachedHeight == height)
    {
        return CATRA_OK; // cache hit
    }

    // Release old resources (size mismatch or first call).
    g_cachedUav.Reset();
    g_cachedBgraTex.Reset();

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.SampleDesc.Quality = 0;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;

    HRESULT hr = device->CreateTexture2D(&desc, nullptr, g_cachedBgraTex.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "nv12_bgra: CreateTexture2D(BGRA %ux%u) hr=0x%08lX",
                   width, height, static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
    uavDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    uavDesc.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
    uavDesc.Texture2D.MipSlice = 0;

    hr = device->CreateUnorderedAccessView(g_cachedBgraTex.Get(), &uavDesc,
                                           g_cachedUav.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "nv12_bgra: CreateUAV(BGRA %ux%u) hr=0x%08lX",
                   width, height, static_cast<unsigned long>(hr));
        g_cachedBgraTex.Reset();
        return CATRA_ERR_DEVICE;
    }

    g_cachedWidth = width;
    g_cachedHeight = height;
    return CATRA_OK;
}

} // anonymous namespace

// ===========================================================================
// Public API
// ===========================================================================

int nv12_bgra_init(ID3D11Device* device)
{
    if (g_ready.load())
    {
        return CATRA_OK; // idempotent
    }

    if (device == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "nv12_bgra_init: null device");
        return CATRA_ERR_INVALID_ARG;
    }

    // Compile the embedded HLSL to DXBC (cs_5_0 — Feature Level 11.0+).
    ComPtr<ID3DBlob> shaderBlob;
    ComPtr<ID3DBlob> errorBlob;
    HRESULT hr = D3DCompile(kNv12ToBgraHlsl, std::strlen(kNv12ToBgraHlsl),
                            "catra_nv12_to_bgra",
                            nullptr, nullptr, "main", "cs_5_0",
                            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                            shaderBlob.GetAddressOf(), errorBlob.GetAddressOf());
    if (FAILED(hr))
    {
        if (errorBlob)
        {
            BackendLog(CATRA_LOG_ERROR,
                       "nv12_bgra_init: D3DCompile failed hr=0x%08lX: %s",
                       static_cast<unsigned long>(hr),
                       static_cast<const char*>(errorBlob->GetBufferPointer()));
        }
        else
        {
            BackendLog(CATRA_LOG_ERROR,
                       "nv12_bgra_init: D3DCompile failed hr=0x%08lX (no error blob)",
                       static_cast<unsigned long>(hr));
        }
        return CATRA_ERR_DEVICE;
    }

    // Create the compute shader object from the compiled blob.
    hr = device->CreateComputeShader(shaderBlob->GetBufferPointer(),
                                     shaderBlob->GetBufferSize(),
                                     nullptr,
                                     g_computeShader.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "nv12_bgra_init: CreateComputeShader hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    g_ready.store(true);
    BackendLog(CATRA_LOG_INFO, "nv12_bgra_init: compute shader compiled OK");
    return CATRA_OK;
}

int nv12_bgra_convert(ID3D11Device* device,
                      ID3D11DeviceContext* ctx,
                      ID3D11Texture2D* nv12ArrayTex,
                      unsigned int arraySlice,
                      unsigned int width,
                      unsigned int height,
                      ID3D11Texture2D** outBgra)
{
    if (outBgra != nullptr)
    {
        *outBgra = nullptr;
    }

    if (!g_ready.load())
    {
        BackendLog(CATRA_LOG_ERROR, "nv12_bgra_convert: not initialized");
        return CATRA_ERR_INIT;
    }

    if (device == nullptr || ctx == nullptr || nv12ArrayTex == nullptr || outBgra == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "nv12_bgra_convert: null argument");
        return CATRA_ERR_INVALID_ARG;
    }

    if (width == 0 || height == 0)
    {
        BackendLog(CATRA_LOG_ERROR, "nv12_bgra_convert: zero dimensions %ux%u", width, height);
        return CATRA_ERR_INVALID_ARG;
    }

    // --- Create SRVs for the NV12 array slice ---------------------------------
    // Y plane: DXGI_FORMAT_R8_UNORM, single array slice.
    D3D11_SHADER_RESOURCE_VIEW_DESC ySrvDesc = {};
    ySrvDesc.Format = DXGI_FORMAT_R8_UNORM;
    ySrvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
    ySrvDesc.Texture2DArray.MostDetailedMip = 0;
    ySrvDesc.Texture2DArray.MipLevels = 1;
    ySrvDesc.Texture2DArray.FirstArraySlice = arraySlice;
    ySrvDesc.Texture2DArray.ArraySize = 1;

    ComPtr<ID3D11ShaderResourceView> ySrv;
    HRESULT hr = device->CreateShaderResourceView(nv12ArrayTex, &ySrvDesc,
                                                  ySrv.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "nv12_bgra_convert: CreateSRV(Y R8 slice=%u) hr=0x%08lX",
                   arraySlice, static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // UV plane: DXGI_FORMAT_R8G8_UNORM, single array slice (same texture,
    // different format view — D3D11 allows format-compatible views of NV12).
    D3D11_SHADER_RESOURCE_VIEW_DESC uvSrvDesc = {};
    uvSrvDesc.Format = DXGI_FORMAT_R8G8_UNORM;
    uvSrvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
    uvSrvDesc.Texture2DArray.MostDetailedMip = 0;
    uvSrvDesc.Texture2DArray.MipLevels = 1;
    uvSrvDesc.Texture2DArray.FirstArraySlice = arraySlice;
    uvSrvDesc.Texture2DArray.ArraySize = 1;

    ComPtr<ID3D11ShaderResourceView> uvSrv;
    hr = device->CreateShaderResourceView(nv12ArrayTex, &uvSrvDesc,
                                          uvSrv.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "nv12_bgra_convert: CreateSRV(UV R8G8 slice=%u) hr=0x%08lX",
                   arraySlice, static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // --- Ensure BGRA output texture + UAV (cached) ----------------------------
    int rc = EnsureBgraOutput(device, width, height);
    if (rc != CATRA_OK)
    {
        return rc;
    }

    // --- Dispatch the compute shader ------------------------------------------
    ID3D11ShaderResourceView* srvs[2] = { ySrv.Get(), uvSrv.Get() };
    ID3D11UnorderedAccessView* uavs[1] = { g_cachedUav.Get() };

    ctx->CSSetShader(g_computeShader.Get(), nullptr, 0);
    ctx->CSSetShaderResources(0, 2, srvs);
    ctx->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);

    // Dispatch covers the full frame; threads beyond the visible area write
    // to valid UAV pixels (the texture is exactly width×height). The shader
    // does not need bounds checking because the dispatch grid is exact.
    unsigned int groupCountX = (width + 15) / 16;
    unsigned int groupCountY = (height + 15) / 16;
    ctx->Dispatch(groupCountX, groupCountY, 1);

    // Unbind to avoid dangling references on the context.
    ID3D11ShaderResourceView* nullSrvs[2] = { nullptr, nullptr };
    ID3D11UnorderedAccessView* nullUavs[1] = { nullptr };
    ctx->CSSetShaderResources(0, 2, nullSrvs);
    ctx->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
    ctx->CSSetShader(nullptr, nullptr, 0);

    // AddRef the cached texture for the caller (they own this reference).
    g_cachedBgraTex->AddRef();
    *outBgra = g_cachedBgraTex.Get();

    return CATRA_OK;
}

void nv12_bgra_shutdown()
{
    g_cachedUav.Reset();
    g_cachedBgraTex.Reset();
    g_cachedWidth = 0;
    g_cachedHeight = 0;
    g_computeShader.Reset();
    g_ready.store(false);
    BackendLog(CATRA_LOG_INFO, "nv12_bgra_shutdown: released");
}

int nv12_bgra_is_ready()
{
    return g_ready.load() ? 1 : 0;
}

} // namespace catra

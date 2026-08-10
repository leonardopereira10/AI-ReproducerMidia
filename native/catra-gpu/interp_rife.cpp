// interp_rife.cpp — RIFE v4 frame interpolation backend (ST-13).
//
// See interp_rife.h for the design overview. The ONNX Runtime code is compiled
// only when CATRA_HAS_ONNXRUNTIME is defined by CMake (i.e. the onnxruntime-gpu
// package was resolved). Without it the entry points degrade to
// CATRA_ERR_NOT_IMPL so the rest of the bridge still builds and loads.
//
// ROOT-CAUSE / DESIGN NOTES
//   * frames_per_pair = floor(target_fps / src_fps) - 1 (ST-30). The floor
//     picks the highest integer rate that does NOT exceed the target, so no
//     frame dropping is needed. Example: 25fps -> 60Hz gives floor(2.4)-1 = 1
//     (output 50fps). When the source already meets/exceeds the target (RN-07)
//     the value clamps to 0 and the context becomes a passthrough that emits
//     zero frames.
//   * Timesteps are i/(N+1) for i in [1..N] (N == frames_per_pair). This splits
//     the [0,1] interval into N+1 equal segments and matches the spec's
//     "t in [1/N'..(N'-1)/N']" with N' == floor(ratio) == N+1.
//   * The DirectML EP is requested first; any failure (unsupported adapter,
//     missing DirectML, RDNA4 driver gap) falls back to the CPU EP so the job
//     still completes (slowly). This is the RN-07 / risk-mitigation decision:
//     quality/availability over speed, offline pipeline.
//   * ST-25 GPU TENSOR I/O: the BGRA <-> planar-RGB-float32 marshalling no
//     longer runs as pixel-by-pixel CPU loops. Two embedded D3D11 compute
//     shaders (one thread per pixel) do the conversion on the GPU; the CPU
//     only performs ONE Map + memcpy of the contiguous float buffer per
//     transfer. The GPU state (shaders, UAV tensor buffer, staging buffer,
//     SRV scratch texture) is created lazily on the first Process call once
//     the frame format is known, and ONLY for BGRA frames. Any init failure
//     (shader compile, buffer alloc) leaves gpuTensorReady == false and the
//     original CPU loops carry the pipeline unchanged — the fallback is the
//     legacy code path, kept intact. The NV12 (hardware decode) conversion
//     stays on the CPU by design (its YUV math is out of scope for ST-25).
//   * KEYED-MUTEX NOTE (ST-15): unlike the FSR backends, RIFE never consumes a
//     shared D3D12 resource. Input frames are read entirely in D3D11 space via
//     a staging CopyResource + Map (TextureToTensor) and written back through a
//     D3D11 staging texture (TensorToTexture). There is no cross-queue D3D12
//     hand-off here, hence no IDXGIKeyedMutex acquire/release is required (the
//     D3D11 immediate-context copies are already ordered on one queue). If a
//     future path feeds RIFE a pooled interop D3D12 texture, it must adopt the
//     same consumer ping-pong the FSR backends use (see d3d_interop.h).

#include "interp_rife.h"
#include "catra_gpu.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
#endif

#if defined(CATRA_HAS_ONNXRUNTIME)
    // ORT_API_MANUAL_INIT: disable the static initialiser in onnxruntime_cxx_api.h
    // that unconditionally calls GetApi(ORT_API_VERSION). The NuGet
    // Microsoft.ML.OnnxRuntime.DirectML package can ship headers newer than the
    // runtime DLL (e.g. header v18 / DLL max API 17), which makes the static
    // init return nullptr and crash on first use. With manual init we negotiate
    // the highest mutually-supported API version at runtime (EnsureOrtApiInitialized).
    #define ORT_API_MANUAL_INIT
    #include <d3d11.h>
    #include <d3dcompiler.h>
    #include <wrl/client.h>
    #include <onnxruntime_cxx_api.h>
    #undef ORT_API_MANUAL_INIT
#endif

// log_msg lives in catra_gpu.cpp; redeclare the minimal surface we need. It is
// defined in the anonymous namespace there, so we route through a small extern
// shim exposed for the backends.
namespace catra {
void BackendLog(int level, const char* fmt, ...);
}

namespace catra {

#if defined(CATRA_HAS_ONNXRUNTIME)

namespace {

using Microsoft::WRL::ComPtr;

// --- ORT API version negotiation -------------------------------------------
//
// The ORT C++ header's static initialiser calls GetApi(ORT_API_VERSION) which
// fails when the loaded onnxruntime.dll is older than the header (the NuGet
// DirectML package has shipped mismatched header/DLL pairs). We define
// ORT_API_MANUAL_INIT (above) and call Ort::InitApi() once with the highest
// API version the DLL actually supports.

void EnsureOrtApiInitialized()
{
    static bool initialized = []() -> bool {
        const OrtApiBase* base = OrtGetApiBase();
        // Walk down from the header's version to 1; GetApi returns nullptr
        // for any version the DLL does not support.
        for (uint32_t v = ORT_API_VERSION; v >= 1; --v)
        {
            const OrtApi* api = base->GetApi(v);
            if (api != nullptr)
            {
                Ort::InitApi(api);
                if (v < ORT_API_VERSION)
                {
                    // Log via fprintf — BackendLog may not be wired yet at
                    // static-init time; by the time InterpRifeCreate calls us
                    // it is fine, but guard anyway.
                    fprintf(stderr,
                            "interp_rife: ORT API negotiated to v%u (header v%d)\n",
                            v, ORT_API_VERSION);
                }
                return true;
            }
        }
        fprintf(stderr, "interp_rife: FATAL — no ORT API version accepted by the DLL\n");
        return false;
    }();
    (void)initialized;
}

// --- Context registry ------------------------------------------------------
//
// Handles handed to C# are dense, non-negative ints. A mutex guards the map;
// the heavy per-context work happens outside the lock (a context is looked up,
// then used by the caller thread).

struct RifeContext; // fwd

std::mutex g_registryMutex;
std::unordered_map<int, std::unique_ptr<RifeContext>> g_contexts;
int g_nextHandle = 0;

// Resolves the RIFE ONNX model path. Candidate order:
//   1. $CATRA_RIFE_MODEL_PATH (explicit override)
//   2. lib/rife/rife_v4.onnx (relative to CWD)
//   3. <module dir>/lib/rife/rife_v4.onnx
//   4. <module dir>/../../../lib/rife/rife_v4.onnx (build-tree layout)
std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty())
    {
        return {};
    }
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), len);
    return w;
}

std::string WideToUtf8(const std::wstring& w)
{
    if (w.empty())
    {
        return {};
    }
    int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), s.data(), len, nullptr, nullptr);
    return s;
}

bool FileExists(const std::wstring& path)
{
    DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
}

std::wstring ModuleDir()
{
    HMODULE self = nullptr;
    // Address of this function as a proxy for the containing module.
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&ModuleDir), &self);
    wchar_t buf[MAX_PATH] = {0};
    DWORD n = GetModuleFileNameW(self, buf, MAX_PATH);
    std::wstring path(buf, n);
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring(L".") : path.substr(0, slash);
}

bool ResolveModelPath(std::wstring& outPath)
{
    const wchar_t* override = _wgetenv(L"CATRA_RIFE_MODEL_PATH");
    if (override != nullptr && override[0] != L'\0')
    {
        std::wstring p(override);
        if (FileExists(p))
        {
            outPath = p;
            return true;
        }
    }

    std::vector<std::wstring> candidates;
    candidates.push_back(L"lib/rife/rife_v4.onnx");
    std::wstring dir = ModuleDir();
    candidates.push_back(dir + L"/lib/rife/rife_v4.onnx");
    candidates.push_back(dir + L"/../../../lib/rife/rife_v4.onnx");

    for (const std::wstring& c : candidates)
    {
        wchar_t full[MAX_PATH] = {0};
        DWORD n = GetFullPathNameW(c.c_str(), MAX_PATH, full, nullptr);
        std::wstring p(n > 0 ? full : c);
        if (FileExists(p))
        {
            outPath = p;
            return true;
        }
    }
    return false;
}

// --- Per-context state -----------------------------------------------------

// ST-25: per-context GPU tensor I/O state. Built lazily on the first Process
// call once the frame format is known (BGRA only); when any piece fails to
// initialize, `ready` stays false and the legacy CPU conversion loops carry
// the pipeline (the ST-25 fallback contract).
struct GpuTensor
{
    bool ready = false;

    // Compute shaders: BGRA texture -> planar float32, planar float32 -> BGRA.
    ComPtr<ID3D11ComputeShader> bgraToFloatCs;
    ComPtr<ID3D11ComputeShader> floatToBgraCs;

    // Dynamic constant buffer { uint width; uint height; } (register b0).
    ComPtr<ID3D11Buffer> constBuf;

    // GPU-side flat tensor buffer (3*W*H R32_FLOAT) + typed UAV (u0 in both
    // shaders). Holds the planar RGB tensor between GPU and CPU.
    ComPtr<ID3D11Buffer> tensorGpuBuf;
    ComPtr<ID3D11UnorderedAccessView> tensorUav;

    // CPU-side staging mirror of tensorGpuBuf (one Map per transfer).
    ComPtr<ID3D11Buffer> tensorCpuBuf;

    // Shader-readable BGRA copy of the incoming frame (frames are not
    // guaranteed to be SRV-bindable at their origin) + its SRV (t0).
    ComPtr<ID3D11Texture2D> bgraScratch;
    ComPtr<ID3D11ShaderResourceView> bgraSrv;
};

struct RifeContext
{
    int srcW = 0;
    int srcH = 0;
    int framesPerPair = 0;   // N intermediate frames; 0 == passthrough (RN-07)
    bool passthrough = false;
    bool stagingReady = false; // lazy init on first Process call
    DXGI_FORMAT stagingFormat = DXGI_FORMAT_UNKNOWN;

    ID3D11Device* device = nullptr;             // borrowed (bridge-owned)
    ID3D11DeviceContext* deviceContext = nullptr; // borrowed

    Ort::Env env{ORT_LOGGING_LEVEL_WARNING, "catra-rife"};
    Ort::SessionOptions sessionOptions;
    Ort::Session session{nullptr};
    Ort::AllocatorWithDefaultOptions allocator;
    Ort::MemoryInfo memInfo{nullptr};

    bool useDml = false;

    // Model I/O metadata (queried, not hardcoded — RIFE exports vary).
    std::vector<std::string> inputNames;
    std::vector<std::string> outputNames;
    std::vector<const char*> inputNamePtrs;
    std::vector<const char*> outputNamePtrs;
    size_t timestepInputIndex = SIZE_MAX; // index of the timestep input, if any

    // Reusable CPU tensor buffers (RGB float32, planar [3 x H x W]).
    std::vector<float> tensorA;
    std::vector<float> tensorB;
    std::vector<float> tensorOut;
    std::vector<float> tensorTimestep;

    // Reusable D3D11 staging textures.
    ComPtr<ID3D11Texture2D> inStagingA;
    ComPtr<ID3D11Texture2D> inStagingB;

    // ST-25: GPU tensor conversion resources (lazy; BGRA frames only).
    GpuTensor gpu;
};

int RegisterContext(std::unique_ptr<RifeContext> ctx)
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    int handle = g_nextHandle++;
    g_contexts.emplace(handle, std::move(ctx));
    return handle;
}

RifeContext* LookupContext(int handle)
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    auto it = g_contexts.find(handle);
    return it == g_contexts.end() ? nullptr : it->second.get();
}

// --- GPU tensor I/O (ST-25) ------------------------------------------------
//
// The BGRA <-> planar-RGB-float32 conversion used to run as pixel-by-pixel
// CPU loops (~5-7 ms each at 1080p, memory-bound; 7 conversions per frame
// pair). These two embedded compute shaders move the pixel math to the GPU
// (one thread per pixel); the CPU only does a single Map + memcpy of the
// contiguous float buffer per transfer. Same embedded-HLSL pattern as
// upscale_fsr1.cpp / nv12_to_bgra_shader.cpp: HLSL string -> D3DCompile
// (cs_5_0, Feature Level 11.0+) at first use.
//
// CHANNEL MAPPING NOTE: for a B8G8R8A8_UNORM typed view the hardware swizzle
// returns SEMANTIC channels — .r is always red (byte 2 in memory), .b is
// always blue (byte 0). The shaders therefore read/write (r,g,b) directly and
// the format itself handles the [B,G,R,A] memory byte order. This keeps the
// GPU path value-equivalent to the CPU loops (which index the bytes as
// b,g,r,a). Alpha is ignored on read and written as 1.0 (255), matching the
// CPU paths.

constexpr const char* kBgraToFloatHlsl = R"HLSL(
// BGRA (B8G8R8A8_UNORM) -> planar RGB float32, normalized [0,1].
Texture2D<float4> inputTex : register(t0);
RWBuffer<float>   output   : register(u0);

cbuffer Constants : register(b0)
{
    uint width;
    uint height;
};

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID)
{
    if (dtid.x >= width || dtid.y >= height)
    {
        return;
    }
    float4 c = inputTex.Load(uint3(dtid.xy, 0)); // UNORM load: already /255
    uint pixelIdx = dtid.y * width + dtid.x;
    uint plane = width * height;
    output[pixelIdx]              = c.r; // R plane
    output[pixelIdx + plane]      = c.g; // G plane
    output[pixelIdx + 2u * plane] = c.b; // B plane
}
)HLSL";

constexpr const char* kFloatToBgraHlsl = R"HLSL(
// Planar RGB float32 -> BGRA (B8G8R8A8_UNORM), clamped to [0,1].
RWBuffer<float>     inputBuf  : register(u0);
RWTexture2D<float4> outputTex : register(u1);

cbuffer Constants : register(b0)
{
    uint width;
    uint height;
};

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID)
{
    if (dtid.x >= width || dtid.y >= height)
    {
        return;
    }
    uint pixelIdx = dtid.y * width + dtid.x;
    uint plane = width * height;
    float r = inputBuf[pixelIdx];
    float g = inputBuf[pixelIdx + plane];
    float b = inputBuf[pixelIdx + 2u * plane];
    // saturate == the CPU clamp01; the UNORM store rounds to nearest, matching
    // the CPU (v * 255 + 0.5) for all practical values.
    outputTex[dtid.xy] = float4(saturate(r), saturate(g), saturate(b), 1.0);
}
)HLSL";

// Constant-buffer payload (register b0; padded to the 16-byte CB register).
struct GpuTensorConstants
{
    UINT width;
    UINT height;
};

// D3DCompile is resolved at runtime from d3dcompiler_47.dll (a Windows 8.1+
// system component) instead of a link-time import: this translation unit is
// also compiled into the catra-interop-test diagnostic executable, which does
// not link d3dcompiler, and ST-25's scope is interp_rife.cpp only. Same
// dynamic-load pattern as the DirectML EP export in InterpRifeCreate.
using D3DCompileFn = HRESULT(WINAPI*)(LPCVOID, SIZE_T, LPCSTR,
                                      const D3D_SHADER_MACRO*, ID3DInclude*,
                                      LPCSTR, LPCSTR, UINT, UINT,
                                      ID3DBlob**, ID3DBlob**);

D3DCompileFn ResolveD3DCompile()
{
    static D3DCompileFn fn = []() -> D3DCompileFn {
        HMODULE mod = LoadLibraryW(L"d3dcompiler_47.dll");
        if (mod == nullptr)
        {
            return nullptr;
        }
        return reinterpret_cast<D3DCompileFn>(GetProcAddress(mod, "D3DCompile"));
    }();
    return fn;
}

// Compiles the two embedded compute shaders and allocates the tensor UAV /
// staging buffers + BGRA scratch texture. Runs once per context on the first
// BGRA Process; any failure logs a warning, releases partial state, and
// leaves ctx->gpu.ready == false so the CPU conversion loops carry on.
bool TryInitGpuTensor(RifeContext* ctx, UINT texW, UINT texH)
{
    GpuTensor& gpu = ctx->gpu;
    ID3D11Device* device = ctx->device;

    // Cleanup + log for any post-compile failure (partial state released).
    auto fail = [&gpu](const char* what, HRESULT hr) -> bool {
        BackendLog(CATRA_LOG_WARN,
                   "interp_rife: GPU tensor init failed at %s (hr=0x%08lX) -> CPU tensor I/O",
                   what, static_cast<unsigned long>(hr));
        gpu = GpuTensor{}; // release partial allocations; ready stays false
        return false;
    };

    D3DCompileFn d3dCompile = ResolveD3DCompile();
    if (d3dCompile == nullptr)
    {
        BackendLog(CATRA_LOG_WARN,
                   "interp_rife: d3dcompiler_47.dll unavailable -> CPU tensor I/O");
        return false;
    }

    // 1. Compile the embedded HLSL to DXBC (cs_5_0 — Feature Level 11.0+,
    //    same target as nv12_to_bgra_shader.cpp).
    ComPtr<ID3DBlob> blob;
    ComPtr<ID3DBlob> err;
    HRESULT hr = d3dCompile(kBgraToFloatHlsl, std::strlen(kBgraToFloatHlsl),
                            "catra_rife_bgra_to_float", nullptr, nullptr,
                            "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                            blob.GetAddressOf(), err.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "interp_rife: D3DCompile(bgra->float) hr=0x%08lX%s%s -> CPU tensor I/O",
                   static_cast<unsigned long>(hr),
                   err ? ": " : "",
                   err ? static_cast<const char*>(err->GetBufferPointer()) : "");
        return false;
    }
    hr = device->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(),
                                     nullptr, gpu.bgraToFloatCs.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateComputeShader(bgra->float)", hr);
    }

    blob.Reset();
    err.Reset();
    hr = d3dCompile(kFloatToBgraHlsl, std::strlen(kFloatToBgraHlsl),
                    "catra_rife_float_to_bgra", nullptr, nullptr,
                    "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                    blob.GetAddressOf(), err.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_WARN,
                   "interp_rife: D3DCompile(float->bgra) hr=0x%08lX%s%s -> CPU tensor I/O",
                   static_cast<unsigned long>(hr),
                   err ? ": " : "",
                   err ? static_cast<const char*>(err->GetBufferPointer()) : "");
        gpu = GpuTensor{};
        return false;
    }
    hr = device->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(),
                                     nullptr, gpu.floatToBgraCs.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateComputeShader(float->bgra)", hr);
    }

    // 2. Dynamic constant buffer ({width,height}); CB ByteWidth must be a
    //    multiple of 16.
    D3D11_BUFFER_DESC cbDesc = {};
    cbDesc.ByteWidth = 16; // sizeof(GpuTensorConstants) rounded up
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = device->CreateBuffer(&cbDesc, nullptr, gpu.constBuf.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateBuffer(const)", hr);
    }

    // 3. GPU-side tensor buffer + typed R32_FLOAT UAV (3*W*H floats, planar).
    const UINT elemCount = 3u * static_cast<UINT>(ctx->srcW)
                              * static_cast<UINT>(ctx->srcH);
    D3D11_BUFFER_DESC bufDesc = {};
    bufDesc.ByteWidth = elemCount * static_cast<UINT>(sizeof(float));
    bufDesc.Usage = D3D11_USAGE_DEFAULT;
    bufDesc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    hr = device->CreateBuffer(&bufDesc, nullptr, gpu.tensorGpuBuf.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateBuffer(tensor gpu)", hr);
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
    uavDesc.Format = DXGI_FORMAT_R32_FLOAT;
    uavDesc.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    uavDesc.Buffer.FirstElement = 0;
    uavDesc.Buffer.NumElements = elemCount;
    hr = device->CreateUnorderedAccessView(gpu.tensorGpuBuf.Get(), &uavDesc,
                                           gpu.tensorUav.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateUAV(tensor)", hr);
    }

    // 4. CPU staging mirror of the tensor buffer (READ for BGRA->float
    //    readback, WRITE for float->BGRA upload).
    bufDesc.Usage = D3D11_USAGE_STAGING;
    bufDesc.BindFlags = 0;
    bufDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE;
    hr = device->CreateBuffer(&bufDesc, nullptr, gpu.tensorCpuBuf.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateBuffer(tensor cpu)", hr);
    }

    // 5. BGRA scratch texture at the FULL frame size (hardware-aligned height
    //    included) + SRV. Frames are CopyResource'd here before the dispatch
    //    because source textures are not guaranteed to be SRV-bindable.
    D3D11_TEXTURE2D_DESC texDesc = {};
    texDesc.Width = texW;
    texDesc.Height = texH;
    texDesc.MipLevels = 1;
    texDesc.ArraySize = 1;
    texDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    texDesc.SampleDesc.Count = 1;
    texDesc.Usage = D3D11_USAGE_DEFAULT;
    texDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    hr = device->CreateTexture2D(&texDesc, nullptr, gpu.bgraScratch.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateTexture2D(bgra scratch)", hr);
    }
    hr = device->CreateShaderResourceView(gpu.bgraScratch.Get(), nullptr,
                                          gpu.bgraSrv.GetAddressOf());
    if (FAILED(hr))
    {
        return fail("CreateSRV(bgra scratch)", hr);
    }

    gpu.ready = true;
    BackendLog(CATRA_LOG_INFO,
               "interp_rife: GPU tensor I/O ready (%dx%d tensor, %ux%u scratch)",
               ctx->srcW, ctx->srcH, texW, texH);
    return true;
}

// Uploads {w,h} into the dynamic constant buffer (MAP_WRITE_DISCARD renames
// the buffer, so no hazard with a previous dispatch reading it).
HRESULT UpdateGpuTensorConstants(ID3D11DeviceContext* dc, ID3D11Buffer* constBuf,
                                 int w, int h)
{
    GpuTensorConstants constants{static_cast<UINT>(w), static_cast<UINT>(h)};
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    HRESULT hr = dc->Map(constBuf, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    if (SUCCEEDED(hr))
    {
        std::memcpy(mapped.pData, &constants, sizeof(constants));
        dc->Unmap(constBuf, 0);
    }
    return hr;
}

// GPU path for TextureToTensor (BGRA only): the compute shader converts the
// frame to planar RGB float32 into tensorGpuBuf, then the CPU does ONE
// CopyResource + Map(READ) + memcpy of the contiguous float buffer (the
// pixel-by-pixel CPU loop is gone).
int TextureToTensorGpu(RifeContext* ctx, ID3D11Texture2D* source, int w, int h,
                       std::vector<float>& tensor)
{
    GpuTensor& gpu = ctx->gpu;
    ID3D11DeviceContext* dc = ctx->deviceContext;

    // GPU copy into the SRV-bindable scratch (device-side, negligible cost).
    dc->CopyResource(gpu.bgraScratch.Get(), source);

    HRESULT hr = UpdateGpuTensorConstants(dc, gpu.constBuf.Get(), w, h);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(const) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // Bind + dispatch (16x16 groups; the shader bounds-checks w x h).
    ID3D11ShaderResourceView* srvs[1] = {gpu.bgraSrv.Get()};
    ID3D11UnorderedAccessView* uavs[1] = {gpu.tensorUav.Get()};
    ID3D11Buffer* cbs[1] = {gpu.constBuf.Get()};
    dc->CSSetShader(gpu.bgraToFloatCs.Get(), nullptr, 0);
    dc->CSSetShaderResources(0, 1, srvs);
    dc->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
    dc->CSSetConstantBuffers(0, 1, cbs);
    dc->Dispatch((static_cast<UINT>(w) + 15) / 16,
                 (static_cast<UINT>(h) + 15) / 16, 1);

    // Unbind before the readback (releases view refs from the context).
    ID3D11ShaderResourceView* nullSrv = nullptr;
    ID3D11UnorderedAccessView* nullUav = nullptr;
    ID3D11Buffer* nullCb = nullptr;
    dc->CSSetShaderResources(0, 1, &nullSrv);
    dc->CSSetUnorderedAccessViews(0, 1, &nullUav, nullptr);
    dc->CSSetConstantBuffers(0, 1, &nullCb);
    dc->CSSetShader(nullptr, nullptr, 0);

    // Single readback of the contiguous tensor. Map(READ) blocks until the
    // dispatch + copy finish — the same contract the legacy staging Map had.
    dc->CopyResource(gpu.tensorCpuBuf.Get(), gpu.tensorGpuBuf.Get());
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    hr = dc->Map(gpu.tensorCpuBuf.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(tensor readback) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    tensor.resize(static_cast<size_t>(w) * static_cast<size_t>(h) * 3);
    std::memcpy(tensor.data(), mapped.pData, tensor.size() * sizeof(float));
    dc->Unmap(gpu.tensorCpuBuf.Get(), 0);
    return CATRA_OK;
}

// GPU path for TensorToTexture: ONE memcpy of the CPU tensor into the staging
// buffer, then the compute shader converts planar float32 -> BGRA directly
// into the output texture (the pixel-by-pixel CPU loop + staging texture are
// gone). The output texture keeps the CPU path's contract (B8G8R8A8_UNORM,
// USAGE_DEFAULT, caller-owned reference) plus BIND_UNORDERED_ACCESS so the
// shader can write it.
int TensorToTextureGpu(RifeContext* ctx, const std::vector<float>& tensor,
                       int w, int h, ID3D11Texture2D** outTexture)
{
    GpuTensor& gpu = ctx->gpu;
    ID3D11Device* device = ctx->device;
    ID3D11DeviceContext* dc = ctx->deviceContext;
    *outTexture = nullptr;

    const size_t bytes = static_cast<size_t>(w) * static_cast<size_t>(h)
                         * 3 * sizeof(float);

    // Upload the contiguous CPU tensor, then mirror it to the GPU buffer.
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    HRESULT hr = dc->Map(gpu.tensorCpuBuf.Get(), 0, D3D11_MAP_WRITE, 0, &mapped);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(tensor upload) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    std::memcpy(mapped.pData, tensor.data(), bytes);
    dc->Unmap(gpu.tensorCpuBuf.Get(), 0);
    dc->CopyResource(gpu.tensorGpuBuf.Get(), gpu.tensorCpuBuf.Get());

    // Output texture (fresh per frame; the caller owns the reference).
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(w);
    desc.Height = static_cast<UINT>(h);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> dst;
    hr = device->CreateTexture2D(&desc, nullptr, dst.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: CreateTexture2D(dst) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }
    ComPtr<ID3D11UnorderedAccessView> dstUav;
    hr = device->CreateUnorderedAccessView(dst.Get(), nullptr, dstUav.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: CreateUAV(dst) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    hr = UpdateGpuTensorConstants(dc, gpu.constBuf.Get(), w, h);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(const) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    // Bind + dispatch: u0 = float tensor, u1 = BGRA texture.
    ID3D11UnorderedAccessView* uavs[2] = {gpu.tensorUav.Get(), dstUav.Get()};
    ID3D11Buffer* cbs[1] = {gpu.constBuf.Get()};
    dc->CSSetShader(gpu.floatToBgraCs.Get(), nullptr, 0);
    dc->CSSetUnorderedAccessViews(0, 2, uavs, nullptr);
    dc->CSSetConstantBuffers(0, 1, cbs);
    dc->Dispatch((static_cast<UINT>(w) + 15) / 16,
                 (static_cast<UINT>(h) + 15) / 16, 1);

    ID3D11UnorderedAccessView* nullUavs[2] = {nullptr, nullptr};
    ID3D11Buffer* nullCb = nullptr;
    dc->CSSetUnorderedAccessViews(0, 2, nullUavs, nullptr);
    dc->CSSetConstantBuffers(0, 1, &nullCb);
    dc->CSSetShader(nullptr, nullptr, 0);

    *outTexture = dst.Detach(); // caller owns the reference
    return CATRA_OK;
}

// --- D3D11 <-> tensor helpers ----------------------------------------------

HRESULT CreateStagingTexture(ID3D11Device* device, int w, int h,
                             DXGI_FORMAT format, ID3D11Texture2D** out)
{
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(w);
    desc.Height = static_cast<UINT>(h);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE;
    return device->CreateTexture2D(&desc, nullptr, out);
}

// Maps a B8G8R8A8/R8G8B8A8 source texture into a planar RGB float32 tensor
// normalized to [0,1]. Returns a CATRA_* code.
// Also supports NV12 (hardware decode format): converts YUV -> RGB on the fly.
int TextureToTensor(RifeContext* ctx, ID3D11Texture2D* staging,
                    ID3D11Texture2D* source, int w, int h,
                    std::vector<float>& tensor)
{
    ID3D11DeviceContext* dc = ctx->deviceContext;

    D3D11_TEXTURE2D_DESC srcDesc = {};
    source->GetDesc(&srcDesc);

    if (srcDesc.Format == DXGI_FORMAT_NV12)
    {
        // NV12 on this AMD driver: CopySubresourceRegion() from a planar
        // source yields zeros and Map() of the UV subresource fails. The
        // reliable read is a whole-texture CopyResource into an NV12 staging
        // followed by a single Map(subresource 0), whose mapped region spans
        // Y then UV contiguously (UV begins h*RowPitch bytes in).
        D3D11_TEXTURE2D_DESC nvDesc = srcDesc;
        nvDesc.Usage = D3D11_USAGE_STAGING;
        nvDesc.BindFlags = 0;
        nvDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        nvDesc.MiscFlags = 0;

        ID3D11Device* dev = nullptr;
        dc->GetDevice(&dev);
        ComPtr<ID3D11Texture2D> nvStaging;
        HRESULT hr2 = dev->CreateTexture2D(&nvDesc, nullptr, nvStaging.GetAddressOf());
        if (dev) dev->Release();
        if (FAILED(hr2))
        {
            BackendLog(CATRA_LOG_ERROR, "interp_rife: NV12 staging alloc hr=0x%08lX",
                       static_cast<unsigned long>(hr2));
            return CATRA_ERR_DEVICE;
        }

        // Map(MAP_READ) blocks until the pending CopyResource completes.
        dc->CopyResource(nvStaging.Get(), source);

        D3D11_MAPPED_SUBRESOURCE mapY = {};
        HRESULT hr = dc->Map(nvStaging.Get(), 0, D3D11_MAP_READ, 0, &mapY);
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(NV12) failed hr=0x%08lX",
                       static_cast<unsigned long>(hr));
            return CATRA_ERR_DEVICE;
        }

        const size_t plane = static_cast<size_t>(w) * static_cast<size_t>(h);
        tensor.resize(plane * 3);
        const size_t pitch = mapY.RowPitch;
        const uint8_t* base = static_cast<const uint8_t*>(mapY.pData);
        const uint8_t* yRows = base;
        const uint8_t* uvRows = base + static_cast<size_t>(h) * pitch;

        for (int y = 0; y < h; ++y)
        {
            const uint8_t* yPx = yRows + static_cast<size_t>(y) * pitch;
            const uint8_t* uvPx = uvRows + static_cast<size_t>(y / 2) * pitch;
            for (int x = 0; x < w; ++x)
            {
                size_t i = static_cast<size_t>(y) * static_cast<size_t>(w) + x;
                float Y = yPx[x] / 255.0f;
                float U = uvPx[(x / 2) * 2 + 0] / 255.0f - 0.5f;
                float V = uvPx[(x / 2) * 2 + 1] / 255.0f - 0.5f;

                // BT.601 YUV -> RGB (full range approximation)
                float R = Y + 1.402f * V;
                float G = Y - 0.344136f * U - 0.714136f * V;
                float B = Y + 1.772f * U;

                tensor[0 * plane + i] = R < 0.0f ? 0.0f : (R > 1.0f ? 1.0f : R);
                tensor[1 * plane + i] = G < 0.0f ? 0.0f : (G > 1.0f ? 1.0f : G);
                tensor[2 * plane + i] = B < 0.0f ? 0.0f : (B > 1.0f ? 1.0f : B);
            }
        }

        dc->Unmap(nvStaging.Get(), 0);
        return CATRA_OK;
    }

    // ST-25 GPU path: compute-shader BGRA -> float32 (TextureToTensorGpu).
    // BGRA only; RGBA8 frames and any non-ready GPU state keep the CPU loop
    // below (the fallback contract).
    if (ctx->gpu.ready && srcDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM)
    {
        return TextureToTensorGpu(ctx, source, w, h, tensor);
    }

    // BGRA/RGBA path (original)
    dc->CopyResource(staging, source);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    HRESULT hr = dc->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(input) failed hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    const size_t plane = static_cast<size_t>(w) * static_cast<size_t>(h);
    tensor.resize(plane * 3);
    const uint8_t* rows = static_cast<const uint8_t*>(mapped.pData);

    for (int y = 0; y < h; ++y)
    {
        const uint8_t* px = rows + static_cast<size_t>(y) * mapped.RowPitch;
        for (int x = 0; x < w; ++x)
        {
            size_t i = static_cast<size_t>(y) * static_cast<size_t>(w) + x;
            uint8_t b = px[x * 4 + 0];
            uint8_t g = px[x * 4 + 1];
            uint8_t r = px[x * 4 + 2];
            tensor[0 * plane + i] = r / 255.0f;
            tensor[1 * plane + i] = g / 255.0f;
            tensor[2 * plane + i] = b / 255.0f;
        }
    }

    dc->Unmap(staging, 0);
    return CATRA_OK;
}

// Writes a planar RGB float32 tensor into a fresh B8G8R8A8 texture returned to
// the caller (refcount 1). Values are clamped to [0,1].
int TensorToTexture(RifeContext* ctx,
                    const std::vector<float>& tensor, int w, int h,
                    ID3D11Texture2D** outTexture)
{
    // ST-25 GPU path: compute-shader float32 -> BGRA (TensorToTextureGpu).
    if (ctx->gpu.ready)
    {
        return TensorToTextureGpu(ctx, tensor, w, h, outTexture);
    }

    ID3D11Device* device = ctx->device;
    ID3D11DeviceContext* dc = ctx->deviceContext;

    // Intermediate CPU-writeable staging texture (output is always BGRA).
    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = CreateStagingTexture(device, w, h, DXGI_FORMAT_B8G8R8A8_UNORM, staging.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: CreateTexture2D(staging) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    hr = dc->Map(staging.Get(), 0, D3D11_MAP_WRITE, 0, &mapped);
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: Map(output) failed hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    const size_t plane = static_cast<size_t>(w) * static_cast<size_t>(h);
    uint8_t* rows = static_cast<uint8_t*>(mapped.pData);
    for (int y = 0; y < h; ++y)
    {
        uint8_t* px = rows + static_cast<size_t>(y) * mapped.RowPitch;
        for (int x = 0; x < w; ++x)
        {
            size_t i = static_cast<size_t>(y) * static_cast<size_t>(w) + x;
            auto clamp01 = [](float v) {
                return v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
            };
            float r = clamp01(tensor[0 * plane + i]);
            float g = clamp01(tensor[1 * plane + i]);
            float b = clamp01(tensor[2 * plane + i]);
            px[x * 4 + 0] = static_cast<uint8_t>(b * 255.0f + 0.5f);
            px[x * 4 + 1] = static_cast<uint8_t>(g * 255.0f + 0.5f);
            px[x * 4 + 2] = static_cast<uint8_t>(r * 255.0f + 0.5f);
            px[x * 4 + 3] = 255;
        }
    }
    dc->Unmap(staging.Get(), 0);

    // Destination GPU texture (default usage, shader-bindable).
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(w);
    desc.Height = static_cast<UINT>(h);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    ComPtr<ID3D11Texture2D> dst;
    hr = device->CreateTexture2D(&desc, nullptr, dst.GetAddressOf());
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: CreateTexture2D(dst) hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

    dc->CopyResource(dst.Get(), staging.Get());
    *outTexture = dst.Detach(); // caller owns the reference
    return CATRA_OK;
}

// Validates a source frame: non-null, 2D, matching dimensions (height may be
// hardware-aligned, e.g. 1088 for 1080), 32bpp RGBA or NV12.
int ValidateFrame(ID3D11Texture2D* tex, int w, int h)
{
    if (tex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    D3D11_TEXTURE2D_DESC desc = {};
    tex->GetDesc(&desc);
    // Width must match exactly; height may be aligned (e.g. 1088 for 1080).
    if (static_cast<int>(desc.Width) != w || static_cast<int>(desc.Height) < h)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interp_rife: frame size %ux%u incompatible with context %dx%d",
                   desc.Width, desc.Height, w, h);
        return CATRA_ERR_INVALID_ARG;
    }
    if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
        desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM &&
        desc.Format != DXGI_FORMAT_NV12)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interp_rife: unsupported frame format %d (need BGRA/RGBA/NV12)",
                   static_cast<int>(desc.Format));
        return CATRA_ERR_INVALID_ARG;
    }
    return CATRA_OK;
}

} // namespace

// --- Public backend API ----------------------------------------------------

bool InterpRifeIsCompiled()
{
    return true;
}

int InterpRifeCreate(ID3D11Device* device,
                     ID3D11DeviceContext* deviceContext,
                     int srcW, int srcH,
                     double srcFps, double targetFps,
                     int method,
                     int* outCtx)
{
    // Negotiate the ORT C API version before any Ort:: object is constructed.
    EnsureOrtApiInitialized();

    if (outCtx != nullptr)
    {
        *outCtx = -1;
    }
    if (device == nullptr || deviceContext == nullptr ||
        srcW <= 0 || srcH <= 0 || srcFps <= 0.0 || targetFps <= 0.0)
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: invalid create args");
        return CATRA_ERR_INVALID_ARG;
    }
    if (method != CATRA_INTERP_RIFE && method != CATRA_INTERP_NONE)
    {
        // Only the RIFE backend exists in ST-13; FSR 3 FG is a later ST.
        BackendLog(CATRA_LOG_WARN, "interp_rife: method %d unavailable, using RIFE", method);
    }

    // ST-30: Use floor() to get the integer rate that doesn't exceed target.
    // Example: source=25fps, target=60Hz -> ratio=2.4 -> floor(2.4)=2 -> output=50fps
    // This ensures output never exceeds the target monitor frequency.
    double ratio = targetFps / srcFps;
    int framesPerPair = static_cast<int>(std::floor(ratio)) - 1;
    if (framesPerPair < 0)
    {
        framesPerPair = 0;
    }

    auto ctx = std::make_unique<RifeContext>();
    ctx->srcW = srcW;
    ctx->srcH = srcH;
    ctx->framesPerPair = framesPerPair;
    ctx->passthrough = (framesPerPair == 0);
    ctx->device = device;
    ctx->deviceContext = deviceContext;

    if (ctx->passthrough)
    {
        // RN-07: nothing to interpolate. No model load, no GPU buffers.
        BackendLog(CATRA_LOG_INFO,
                   "interp_rife: src_fps=%.3f >= target_fps=%.3f -> passthrough (0 frames/pair)",
                   srcFps, targetFps);
        int handle = RegisterContext(std::move(ctx));
        if (outCtx != nullptr)
        {
            *outCtx = handle;
        }
        return CATRA_OK;
    }

    // --- Locate + load the ONNX model -------------------------------------
    std::wstring modelPath;
    if (!ResolveModelPath(modelPath))
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interp_rife: rife_v4.onnx not found (set CATRA_RIFE_MODEL_PATH "
                   "or run scripts/build-native.ps1 to download it)");
        return CATRA_ERR_INIT;
    }

    ctx->memInfo = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);

    // --- Execution providers: DirectML first, CPU fallback ----------------
    ctx->sessionOptions.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
#if defined(USE_DML)
    // The DirectML EP is registered via the C export from the ORT DirectML
    // build. The C++ wrapper method (SessionOptions::AppendExecutionProvider_DML)
    // does NOT exist in the Microsoft.ML.OnnxRuntime.DirectML NuGet package —
    // only the flat C export does. We load it dynamically so there is no
    // link-time dependency on the export (graceful fallback if absent).
    using DmlAppendFn = OrtStatus*(__stdcall*)(OrtSessionOptions*, int);
    HMODULE ortMod = GetModuleHandleW(L"onnxruntime.dll");
    auto dmlAppend = ortMod != nullptr
        ? reinterpret_cast<DmlAppendFn>(
              GetProcAddress(ortMod, "OrtSessionOptionsAppendExecutionProvider_DML"))
        : nullptr;
    if (dmlAppend != nullptr)
    {
        OrtSessionOptions* rawOpts = ctx->sessionOptions;
        OrtStatus* st = dmlAppend(rawOpts, 0 /* device_id: primary adapter */);
        if (st == nullptr)
        {
            ctx->useDml = true;
        }
        else
        {
            const char* msg = Ort::GetApi().GetErrorMessage(st);
            BackendLog(CATRA_LOG_WARN,
                       "interp_rife: DirectML EP unavailable (%s) -> CPU fallback",
                       msg != nullptr ? msg : "unknown");
            Ort::GetApi().ReleaseStatus(st);
            ctx->useDml = false;
        }
    }
    else
    {
        BackendLog(CATRA_LOG_WARN,
                   "interp_rife: DirectML EP export not found in onnxruntime.dll -> CPU fallback");
        ctx->useDml = false;
    }
#endif
    // CPU EP is always registered by default in ONNX Runtime; no explicit
    // AppendExecutionProvider_CPU call needed (the method does not exist in
    // the 1.18 C++ API). When DML is unavailable the session simply uses CPU.

    try
    {
        ctx->session = Ort::Session(ctx->env, modelPath.c_str(), ctx->sessionOptions);
    }
    catch (const Ort::Exception& e)
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: failed to load model: %s", e.what());
        return CATRA_ERR_INIT;
    }

    BackendLog(CATRA_LOG_INFO,
               "interp_rife: model loaded (%s), EP=%s, %dx%d, %d frames/pair (ratio=%.3f, floor mode)",
               WideToUtf8(modelPath).c_str(),
               ctx->useDml ? "DirectML" : "CPU",
               srcW, srcH, framesPerPair, ratio);

    // --- Query model I/O names (RIFE exports vary; do not hardcode) -------
    size_t inputCount = ctx->session.GetInputCount();
    if (inputCount < 2)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interp_rife: model has %zu inputs (need >=2: img0,img1)", inputCount);
        return CATRA_ERR_INIT;
    }
    for (size_t i = 0; i < inputCount; ++i)
    {
        Ort::AllocatedStringPtr name =
            ctx->session.GetInputNameAllocated(i, ctx->allocator);
        ctx->inputNames.emplace_back(name.get());
    }
    size_t outputCount = ctx->session.GetOutputCount();
    if (outputCount < 1)
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: model has no outputs");
        return CATRA_ERR_INIT;
    }
    for (size_t i = 0; i < outputCount; ++i)
    {
        Ort::AllocatedStringPtr name =
            ctx->session.GetOutputNameAllocated(i, ctx->allocator);
        ctx->outputNames.emplace_back(name.get());
    }
    for (const std::string& n : ctx->inputNames)
    {
        ctx->inputNamePtrs.push_back(n.c_str());
    }
    for (const std::string& n : ctx->outputNames)
    {
        ctx->outputNamePtrs.push_back(n.c_str());
    }

    // Locate the arbitrary-timestep input. RIFE exports vary, so prefer a
    // name match ("timestep" / "time" / "t", case-insensitive) validated by a
    // scalar/single-element shape; only when no name matches do we fall back
    // to the historical convention of "the 3rd input" (index 2).
    if (inputCount >= 3)
    {
        auto toLower = [](std::string s) {
            std::transform(s.begin(), s.end(), s.begin(),
                           [](unsigned char c) {
                               return static_cast<char>(std::tolower(c));
                           });
            return s;
        };
        // True when the input can receive the shape-{1} timestep tensor we
        // feed (scalar or single-element). Query failures give the candidate
        // the benefit of the doubt rather than dropping the timestep feed.
        auto acceptsScalar = [&](size_t idx) -> bool {
            try
            {
                Ort::TypeInfo typeInfo = ctx->session.GetInputTypeInfo(idx);
                return typeInfo.GetTensorTypeAndShapeInfo().GetElementCount() == 1;
            }
            catch (const Ort::Exception& e)
            {
                BackendLog(CATRA_LOG_DEBUG,
                           "interp_rife: input %zu shape probe failed (%s); assuming scalar",
                           idx, e.what());
                return true;
            }
        };

        static const char* const kTimestepNames[] = {"timestep", "time", "t"};
        bool found = false;
        for (size_t i = 0; i < inputCount && !found; ++i)
        {
            const std::string name = toLower(ctx->inputNames[i]);
            for (const char* candidate : kTimestepNames)
            {
                if (name == candidate && acceptsScalar(i))
                {
                    ctx->timestepInputIndex = i;
                    found = true;
                    BackendLog(CATRA_LOG_INFO,
                               "interp_rife: timestep input '%s' resolved at index %zu",
                               ctx->inputNames[i].c_str(), i);
                    break;
                }
            }
        }
        if (!found)
        {
            ctx->timestepInputIndex = 2;
            BackendLog(CATRA_LOG_WARN,
                       "interp_rife: no timestep input named timestep/time/t -> "
                       "falling back to index 2 (shape_ok=%d)",
                       acceptsScalar(2) ? 1 : 0);
        }
    }

    // --- Preallocate reusable buffers -------------------------------------
    const size_t plane = static_cast<size_t>(srcW) * static_cast<size_t>(srcH);
    ctx->tensorA.resize(plane * 3);
    ctx->tensorB.resize(plane * 3);
    ctx->tensorOut.resize(plane * 3);

    // Staging textures are created lazily on the first Process call once we
    // know the actual frame format (NV12 from hardware decode, or BGRA from
    // software decode / interop).

    int handle = RegisterContext(std::move(ctx));
    if (outCtx != nullptr)
    {
        *outCtx = handle;
    }
    return CATRA_OK;
}

int InterpRifeProcess(int ctxHandle,
                      void* frameA, void* frameB,
                      void** outFrames, int* outCount)
{
    fprintf(stderr, "interp_rife: Process ENTER ctx=%d frameA=%p frameB=%p\n",
            ctxHandle, frameA, frameB);
    fflush(stderr);
    if (outFrames != nullptr)
    {
        *outFrames = nullptr;
    }
    if (outCount != nullptr)
    {
        *outCount = 0;
    }

    RifeContext* ctx = LookupContext(ctxHandle);
    if (ctx == nullptr)
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: unknown context %d", ctxHandle);
        return CATRA_ERR_CONTEXT;
    }

    // RN-07 passthrough: zero intermediate frames, success.
    if (ctx->passthrough)
    {
        return CATRA_OK;
    }
    if (frameA == nullptr || frameB == nullptr || outFrames == nullptr || outCount == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }

    ID3D11Texture2D* texA = static_cast<ID3D11Texture2D*>(frameA);
    ID3D11Texture2D* texB = static_cast<ID3D11Texture2D*>(frameB);

    fprintf(stderr, "interp_rife: ValidateFrame A...\n"); fflush(stderr);
    int rc = ValidateFrame(texA, ctx->srcW, ctx->srcH);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "interp_rife: ValidateFrame A FAILED rc=%d\n", rc); fflush(stderr);
        return rc;
    }
    fprintf(stderr, "interp_rife: ValidateFrame B...\n"); fflush(stderr);
    rc = ValidateFrame(texB, ctx->srcW, ctx->srcH);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "interp_rife: ValidateFrame B FAILED rc=%d\n", rc); fflush(stderr);
        return rc;
    }
    fprintf(stderr, "interp_rife: frames validated OK\n"); fflush(stderr);

    // Lazy staging texture creation: detect format from the first frame and
    // create matching staging textures (NV12 for hardware decode, BGRA for
    // software / interop).
    if (!ctx->stagingReady)
    {
        D3D11_TEXTURE2D_DESC desc = {};
        texA->GetDesc(&desc);
        ctx->stagingFormat = desc.Format;

        // Staging textures must match the SOURCE dimensions exactly (hardware-
        // aligned, e.g. 1920x1088 for 1080p NV12) so CopyResource succeeds.
        // TextureToTensor only reads srcW x srcH valid pixels.
        HRESULT hr = CreateStagingTexture(ctx->device,
                                          static_cast<int>(desc.Width),
                                          static_cast<int>(desc.Height),
                                          ctx->stagingFormat, ctx->inStagingA.GetAddressOf());
        if (SUCCEEDED(hr))
        {
            hr = CreateStagingTexture(ctx->device,
                                      static_cast<int>(desc.Width),
                                      static_cast<int>(desc.Height),
                                      ctx->stagingFormat, ctx->inStagingB.GetAddressOf());
        }
        if (FAILED(hr))
        {
            BackendLog(CATRA_LOG_ERROR, "interp_rife: staging texture alloc hr=0x%08lX (format=%d, %ux%u)",
                       static_cast<unsigned long>(hr), static_cast<int>(ctx->stagingFormat),
                       desc.Width, desc.Height);
            return CATRA_ERR_DEVICE;
        }
        ctx->stagingReady = true;
        fprintf(stderr, "interp_rife: staging textures created (format=%d, %ux%u)\n",
                static_cast<int>(ctx->stagingFormat), desc.Width, desc.Height);

        // ST-25: BGRA frames convert through compute shaders on the GPU.
        // NV12 (hardware decode) and RGBA8 keep the CPU loops. Init failure
        // is non-fatal: gpu.ready stays false and the CPU paths carry on.
        if (ctx->stagingFormat == DXGI_FORMAT_B8G8R8A8_UNORM)
        {
            TryInitGpuTensor(ctx, desc.Width, desc.Height);
        }
    }

    fprintf(stderr, "interp_rife: TextureToTensor A...\n"); fflush(stderr);
    rc = TextureToTensor(ctx, ctx->inStagingA.Get(), texA,
                         ctx->srcW, ctx->srcH, ctx->tensorA);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "interp_rife: TextureToTensor A FAILED rc=%d\n", rc); fflush(stderr);
        return rc;
    }
    fprintf(stderr, "interp_rife: TextureToTensor B...\n"); fflush(stderr);
    rc = TextureToTensor(ctx, ctx->inStagingB.Get(), texB,
                         ctx->srcW, ctx->srcH, ctx->tensorB);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "interp_rife: TextureToTensor B FAILED rc=%d\n", rc); fflush(stderr);
        return rc;
    }
    fprintf(stderr, "interp_rife: tensors ready, starting inference loop N=%d\n", ctx->framesPerPair); fflush(stderr);

    // ST-30: floor mode — fixed frames per pair, no frame dropping.
    const int N = ctx->framesPerPair;
    const int64_t w = ctx->srcW;
    const int64_t h = ctx->srcH;
    const std::array<int64_t, 4> frameShape{1, 3, h, w};

    // Output array (caller-owned): allocated with `new void*[N]` (CRT heap) and
    // freed by the caller via catra_free (`delete[] void**`); each contained
    // texture is AddRef'd (refcount 1) and released by the caller via
    // catra_release_texture BEFORE the array is freed.
    std::unique_ptr<void*[]> results(new void*[static_cast<size_t>(N)]);
    int produced = 0;

    // Unified cleanup: releases every texture produced so far. Must run on
    // ALL error exits from the inference loop (early returns AND exceptions)
    // so a mid-pair failure never leaks ID3D11Texture2D references. *outFrames
    // stays null on every failure path (set at function entry, only assigned
    // after results.release() on success).
    auto releaseProduced = [&]() noexcept {
        for (int k = 0; k < produced; ++k)
        {
            static_cast<ID3D11Texture2D*>(results[static_cast<size_t>(k)])->Release();
            results[static_cast<size_t>(k)] = nullptr;
        }
        produced = 0;
    };

    try
    {
        for (int i = 1; i <= N; ++i)
        {
            const float t = static_cast<float>(i) / static_cast<float>(N + 1);

            Ort::Value inA = Ort::Value::CreateTensor<float>(
                ctx->memInfo, ctx->tensorA.data(), ctx->tensorA.size(),
                frameShape.data(), frameShape.size());
            Ort::Value inB = Ort::Value::CreateTensor<float>(
                ctx->memInfo, ctx->tensorB.data(), ctx->tensorB.size(),
                frameShape.data(), frameShape.size());

            std::vector<Ort::Value> inputs;
            inputs.push_back(std::move(inA));
            inputs.push_back(std::move(inB));

            // Optional arbitrary-timestep input.
            Ort::Value tsValue{nullptr};
            if (ctx->timestepInputIndex != SIZE_MAX)
            {
                ctx->tensorTimestep.assign(1, t);
                const std::array<int64_t, 1> tsShape{1};
                tsValue = Ort::Value::CreateTensor<float>(
                    ctx->memInfo, ctx->tensorTimestep.data(), ctx->tensorTimestep.size(),
                    tsShape.data(), tsShape.size());
                inputs.push_back(std::move(tsValue));
            }

            auto outputs = ctx->session.Run(
                Ort::RunOptions{nullptr},
                ctx->inputNamePtrs.data(), inputs.data(), inputs.size(),
                ctx->outputNamePtrs.data(), ctx->outputNamePtrs.size());

            if (outputs.empty())
            {
                BackendLog(CATRA_LOG_ERROR, "interp_rife: Run returned no outputs (t=%.3f)", t);
                releaseProduced();
                return CATRA_ERR_DEVICE;
            }

            // Copy the model output (RGB float32) into the reusable buffer.
            const float* outData = outputs[0].GetTensorData<float>();
            size_t pixelCount = static_cast<size_t>(w) * static_cast<size_t>(h) * 3;
            std::copy(outData, outData + pixelCount, ctx->tensorOut.begin());

            ID3D11Texture2D* outTex = nullptr;
            // BGRA output: the AMD RDNA 4 driver does not correctly share
            // D3D11-created NV12 (planar) textures into D3D12 via NT handles
            // (verified with tools/interop_readback_test.cpp: the D3D12 view
            // reads zeros). BGRA shares cleanly, so the whole pipeline runs
            // BGRA and the AMF encoder is initialized with AMF_SURFACE_BGRA.
            rc = TensorToTexture(ctx, ctx->tensorOut,
                                 ctx->srcW, ctx->srcH, &outTex);
            if (rc != CATRA_OK)
            {
                releaseProduced();
                return rc;
            }
            results[static_cast<size_t>(produced++)] = outTex;
        }
    }
    catch (const Ort::Exception& e)
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: inference failed: %s", e.what());
        releaseProduced();
        return CATRA_ERR_DEVICE;
    }
    catch (...)
    {
        // Non-ORT failures (e.g. std::bad_alloc from tensor buffer growth).
        // Free the partial output set so nothing leaks, then rethrow: the
        // C ABI boundary in catra_gpu.cpp converts the exception into
        // CATRA_ERR_UNKNOWN (an exception must never unwind further on its
        // own — releaseProduced is noexcept-safe: Release() does not throw).
        releaseProduced();
        throw;
    }

    *outFrames = results.release();
    *outCount = produced;
    return CATRA_OK;
}

void InterpRifeDestroy(int ctxHandle)
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    g_contexts.erase(ctxHandle); // unique_ptr frees session/buffers/textures
}

void InterpRifeDestroyAll()
{
    std::lock_guard<std::mutex> lock(g_registryMutex);
    g_contexts.clear();
}

#else // !CATRA_HAS_ONNXRUNTIME ---------------------------------------------

bool InterpRifeIsCompiled()
{
    return false;
}

int InterpRifeCreate(ID3D11Device*, ID3D11DeviceContext*,
                     int, int, double, double, int, int* outCtx)
{
    if (outCtx != nullptr)
    {
        *outCtx = -1;
    }
    BackendLog(CATRA_LOG_ERROR,
               "interp_rife: built without ONNX Runtime (CATRA_HAS_ONNXRUNTIME undefined)");
    return CATRA_ERR_NOT_IMPL;
}

int InterpRifeProcess(int, void*, void*, void** outFrames, int* outCount)
{
    if (outFrames != nullptr)
    {
        *outFrames = nullptr;
    }
    if (outCount != nullptr)
    {
        *outCount = 0;
    }
    return CATRA_ERR_NOT_IMPL;
}

void InterpRifeDestroy(int) {}
void InterpRifeDestroyAll() {}

#endif // CATRA_HAS_ONNXRUNTIME

} // namespace catra

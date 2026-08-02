// interp_rife.cpp — RIFE v4 frame interpolation backend (ST-13).
//
// See interp_rife.h for the design overview. The ONNX Runtime code is compiled
// only when CATRA_HAS_ONNXRUNTIME is defined by CMake (i.e. the onnxruntime-gpu
// package was resolved). Without it the entry points degrade to
// CATRA_ERR_NOT_IMPL so the rest of the bridge still builds and loads.
//
// ROOT-CAUSE / DESIGN NOTES
//   * frames_per_pair = ceil(target_fps / src_fps) - 1. For 24->135 this is
//     ceil(5.625)-1 = 5; for 24->55 it is ceil(2.2917)-1 = 2. When the source
//     already meets/exceeds the target (RN-07) the value is 0 and the context
//     becomes a passthrough that emits zero frames.
//   * Timesteps are i/(N+1) for i in [1..N] (N == frames_per_pair). This splits
//     the [0,1] interval into N+1 equal segments and matches the spec's
//     "t in [1/N'..(N'-1)/N']" with N' == ceil(ratio) == N+1.
//   * The DirectML EP is requested first; any failure (unsupported adapter,
//     missing DirectML, RDNA4 driver gap) falls back to the CPU EP so the job
//     still completes (slowly). This is the RN-07 / risk-mitigation decision:
//     quality/availability over speed, offline pipeline.
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
    #include <d3d11.h>
    #include <wrl/client.h>
    #include <onnxruntime_cxx_api.h>
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

struct RifeContext
{
    int srcW = 0;
    int srcH = 0;
    int framesPerPair = 0;   // N intermediate frames; 0 == passthrough (RN-07)
    bool passthrough = false;

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

// --- D3D11 <-> tensor helpers ----------------------------------------------

HRESULT CreateStagingTexture(ID3D11Device* device, int w, int h,
                             ID3D11Texture2D** out)
{
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(w);
    desc.Height = static_cast<UINT>(h);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE;
    return device->CreateTexture2D(&desc, nullptr, out);
}

// Maps a B8G8R8A8/R8G8B8A8 source texture into a planar RGB float32 tensor
// normalized to [0,1]. Returns a CATRA_* code.
int TextureToTensor(ID3D11DeviceContext* dc, ID3D11Texture2D* staging,
                    ID3D11Texture2D* source, int w, int h,
                    std::vector<float>& tensor)
{
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
int TensorToTexture(ID3D11Device* device, ID3D11DeviceContext* dc,
                    const std::vector<float>& tensor, int w, int h,
                    ID3D11Texture2D** outTexture)
{
    // Intermediate CPU-writeable staging texture.
    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = CreateStagingTexture(device, w, h, staging.GetAddressOf());
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

// Validates a source frame: non-null, 2D, matching dimensions, 32bpp RGBA.
int ValidateFrame(ID3D11Texture2D* tex, int w, int h)
{
    if (tex == nullptr)
    {
        return CATRA_ERR_INVALID_ARG;
    }
    D3D11_TEXTURE2D_DESC desc = {};
    tex->GetDesc(&desc);
    if (static_cast<int>(desc.Width) != w || static_cast<int>(desc.Height) != h)
    {
        BackendLog(CATRA_LOG_ERROR,
                   "interp_rife: frame size %ux%u != context %dx%d",
                   desc.Width, desc.Height, w, h);
        return CATRA_ERR_INVALID_ARG;
    }
    if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
        desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM)
    {
        // NV12 decode frames are converted upstream once ST-15/17 interop lands.
        BackendLog(CATRA_LOG_ERROR,
                   "interp_rife: unsupported frame format %d (need BGRA/RGBA)",
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

    // frames_per_pair = ceil(target/src) - 1; RN-07 skip when source >= target.
    double ratio = targetFps / srcFps;
    int framesPerPair = static_cast<int>(std::ceil(ratio)) - 1;
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
    try
    {
        // Device id 0 == the primary adapter (the bridge's D3D11 adapter).
        ctx->sessionOptions.AppendExecutionProvider_DML(0);
        ctx->useDml = true;
    }
    catch (const Ort::Exception& e)
    {
        BackendLog(CATRA_LOG_WARN,
                   "interp_rife: DirectML EP unavailable (%s) -> CPU fallback", e.what());
        ctx->useDml = false;
    }
#endif
    if (!ctx->useDml)
    {
        try
        {
            ctx->sessionOptions.AppendExecutionProvider_CPU({});
        }
        catch (const Ort::Exception&)
        {
            // CPU is always registered; ignore.
        }
    }

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
               "interp_rife: model loaded (%s), EP=%s, %dx%d, %d frames/pair (ratio=%.3f)",
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

    HRESULT hr = CreateStagingTexture(device, srcW, srcH, ctx->inStagingA.GetAddressOf());
    if (SUCCEEDED(hr))
    {
        hr = CreateStagingTexture(device, srcW, srcH, ctx->inStagingB.GetAddressOf());
    }
    if (FAILED(hr))
    {
        BackendLog(CATRA_LOG_ERROR, "interp_rife: staging texture alloc hr=0x%08lX",
                   static_cast<unsigned long>(hr));
        return CATRA_ERR_DEVICE;
    }

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

    int rc = ValidateFrame(texA, ctx->srcW, ctx->srcH);
    if (rc != CATRA_OK)
    {
        return rc;
    }
    rc = ValidateFrame(texB, ctx->srcW, ctx->srcH);
    if (rc != CATRA_OK)
    {
        return rc;
    }

    rc = TextureToTensor(ctx->deviceContext, ctx->inStagingA.Get(), texA,
                         ctx->srcW, ctx->srcH, ctx->tensorA);
    if (rc != CATRA_OK)
    {
        return rc;
    }
    rc = TextureToTensor(ctx->deviceContext, ctx->inStagingB.Get(), texB,
                         ctx->srcW, ctx->srcH, ctx->tensorB);
    if (rc != CATRA_OK)
    {
        return rc;
    }

    const int N = ctx->framesPerPair;
    const int64_t w = ctx->srcW;
    const int64_t h = ctx->srcH;
    const std::array<int64_t, 4> frameShape{1, 3, h, w};

    // Output array (caller frees with delete[] + Release on each texture).
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
            size_t outCount = static_cast<size_t>(w) * static_cast<size_t>(h) * 3;
            std::copy(outData, outData + outCount, ctx->tensorOut.begin());

            ID3D11Texture2D* outTex = nullptr;
            rc = TensorToTexture(ctx->device, ctx->deviceContext, ctx->tensorOut,
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

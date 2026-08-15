// ffx_runtime.cpp — runtime loader for the FidelityFX (FFX) API 2.x
// (SPRINT_04, subtask 01). See ffx_runtime.h for the contract.
//
// NOTE on _WINDOWS: ffx_runtime.h defines _WINDOWS before including
// ffx_api_loader.h (gotcha 2a — MSVC does not define it, and ffxLoadFunctions
// compiles to an empty body without it, leaving all 5 pointers NULL).

#include "ffx_runtime.h"

#include "catra_gpu.h" // CATRA_LOG_* level constants

#include <libloaderapi.h>

#include <mutex>

// Backend log shim, defined in catra_gpu.cpp (same sink as the C ABI
// diagnostics; thread-safe via g_logMutex). Forward-declared exactly like
// d3d_interop.cpp does.
namespace catra {
void BackendLog(int level, const char* fmt, ...);
} // namespace catra

namespace {

// Loader module name. Loaded via EXPLICIT FULL PATH (module dir + name) with
// LoadLibraryExW + LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR: in the real app, the FFX
// DLLs live in bin/.../runtimes/win-x64/native/ next to catra-gpu.dll, a
// subdirectory that is NOT covered by the process default search order (exe
// dir, system dirs, PATH) — a bare LoadLibraryA would fail with err=126.
// LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR also makes the loader's OWN dependencies
// (amd_fidelityfx_upscaler_dx12.dll etc.) resolve from the loader's directory.
constexpr const char* kLoaderDllName = "amd_fidelityfx_loader_dx12.dll";

// List A1: the 8 FFX runtime DLLs that must sit next to catra-gpu.dll.
constexpr const char* kDependencyDlls[] = {
    "amd_fidelityfx_loader_dx12.dll",
    "amd_fidelityfx_upscaler_dx12.dll",
    "amd_fidelityfx_framegeneration_dx12.dll",
    "amd_ags_x64.dll",
    "amd_acs_x64.dll",
    "D3D12Core.dll",
    "dxcompiler.dll",
    "dxil.dll",
};

// Loader state, guarded by g_mutex (same pattern as g_logMutex in
// catra_gpu.cpp). Load/Unload are idempotent.
std::mutex g_mutex;
HMODULE g_loaderModule = nullptr;
ffxFunctions g_functions = {};
bool g_loaded = false;

// Directory of the module this code is compiled into (catra-gpu.dll in the
// shipped bridge; the test exe in the smoke test). Uses the address of a
// function in THIS translation unit as the module proxy — same trick as
// ModuleDir() in interp_rife.cpp.
std::wstring ModuleDirWide()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&ModuleDirWide), &self);
    wchar_t buf[MAX_PATH] = {0};
    const DWORD n = GetModuleFileNameW(self, buf, MAX_PATH);
    std::wstring path(buf, n);
    const size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring(L".") : path.substr(0, slash);
}

std::wstring AsciiToWide(const char* s)
{
    std::wstring w;
    while (*s != '\0')
    {
        w.push_back(static_cast<wchar_t>(*s));
        ++s;
    }
    return w;
}

// Creates a transient DX12 device to prove a DX12 adapter exists. Tries
// D3D_FEATURE_LEVEL_11_0 first (the minimum D3D12 feature level), then 12_0.
bool TryCreateTransientDx12Device(ID3D12Device** outDevice)
{
    HRESULT hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0,
                                   IID_PPV_ARGS(outDevice));
    if (FAILED(hr))
    {
        hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0,
                               IID_PPV_ARGS(outDevice));
    }
    return SUCCEEDED(hr);
}

// FFX -> BackendLog sink (subtask 01, item 2d). Invoked from FFX threads:
// no heap allocation, no exceptions, stack buffer only. catra::BackendLog is
// thread-safe (g_logMutex).
void FfxLogSink(uint32_t type, const wchar_t* message)
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
    catra::BackendLog(level, "[FFX] %s", buffer);
}

} // namespace

namespace catra::ffx {

bool FfxRuntime::Load()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_loaded)
    {
        return true; // idempotent no-op
    }

    // Explicit full path from THIS module's directory (catra-gpu.dll lives in
    // the same runtimes/win-x64/native/ dir as the FFX DLLs after deploy).
    // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR requires an absolute path and resolves
    // the loader's own dependencies from the loader's directory — mandatory
    // because the FFX dir is outside the process default DLL search order.
    const std::wstring fullPath = ModuleDirWide() + L"\\" + AsciiToWide(kLoaderDllName);
    HMODULE mod = LoadLibraryExW(fullPath.c_str(), nullptr,
                                 LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
    if (mod == nullptr)
    {
        // Fallback for pre-Windows-8 systems without KB2533623, where the
        // DLL_LOAD_DIR flag is rejected: default search (0) still resolves the
        // loader itself via the absolute path; its internal dependencies then
        // fall back to the standard search order.
        const DWORD err = GetLastError();
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] LoadLibraryExW(%ls, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR) "
                          "failed, err=%lu; retrying with default search order",
                          fullPath.c_str(), err);
        mod = LoadLibraryExW(fullPath.c_str(), nullptr, 0);
    }
    if (mod == nullptr)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] LoadLibraryExW(%ls) failed, err=%lu",
                          fullPath.c_str(), GetLastError());
        return false;
    }

    ffxFunctions funcs = {};
    ffxLoadFunctions(&funcs, mod);
    if (funcs.CreateContext == nullptr || funcs.DestroyContext == nullptr ||
        funcs.Configure == nullptr || funcs.Query == nullptr ||
        funcs.Dispatch == nullptr)
    {
        catra::BackendLog(CATRA_LOG_ERROR,
                          "[FFX] %s loaded but one or more ffx exports are "
                          "missing; unloading",
                          kLoaderDllName);
        FreeLibrary(mod);
        return false;
    }

    g_loaderModule = mod;
    g_functions = funcs;
    g_loaded = true;
    catra::BackendLog(CATRA_LOG_INFO,
                      "[FFX] %s loaded, 5/5 ffx exports resolved",
                      kLoaderDllName);
    return true;
}

void FfxRuntime::Unload()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_loaded)
    {
        return; // idempotent no-op
    }
    FreeLibrary(g_loaderModule);
    g_loaderModule = nullptr;
    g_functions = {};
    g_loaded = false;
    catra::BackendLog(CATRA_LOG_INFO, "[FFX] loader unloaded");
}

bool FfxRuntime::IsLoaded()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_loaded;
}

const ffxFunctions& FfxRuntime::Functions()
{
    // Callers must check IsLoaded() first; the returned reference stays valid
    // until Unload().
    return g_functions;
}

bool FfxRuntime::ProbeDependencyDlls()
{
    const std::wstring dir = ModuleDirWide();
    for (const char* name : kDependencyDlls)
    {
        const std::wstring path = dir + L"\\" + AsciiToWide(name);
        const DWORD attrs = GetFileAttributesW(path.c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES ||
            (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            catra::BackendLog(CATRA_LOG_WARN,
                              "[FFX] dependency DLL missing next to module: %s",
                              name);
            return false;
        }
    }
    return true;
}

bool FfxRuntime::ProbeDx12Adapter()
{
    ID3D12Device* device = nullptr;
    if (!TryCreateTransientDx12Device(&device))
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] no DX12 adapter available "
                          "(D3D12CreateDevice failed at FL 11.0/12.0)");
        return false;
    }
    device->Release();
    return true;
}

bool FfxRuntime::IsAvailable()
{
    if (!Load())
    {
        return false;
    }
    if (!ProbeDependencyDlls())
    {
        return false;
    }

    // Version probe needs a device; create a transient one and ask the loader
    // for the upscale provider list. 0 versions == missing provider / runtime
    // mismatch -> unavailable (fail-safe, item 2c).
    ID3D12Device* device = nullptr;
    if (!TryCreateTransientDx12Device(&device))
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] IsAvailable: no DX12 adapter for version probe");
        return false;
    }

    std::vector<std::string> names;
    const int count = QueryUpscaleVersions(device, names);
    device->Release();
    if (count <= 0)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] IsAvailable: no FFX upscale provider versions "
                          "reported (runtime mismatch?)");
        return false;
    }
    return true;
}

int FfxRuntime::QueryUpscaleVersions(ID3D12Device* device,
                                     std::vector<std::string>& outNames)
{
    outNames.clear();

    if (!IsLoaded())
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] QueryUpscaleVersions: loader not loaded");
        return 0;
    }
    if (device == nullptr)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] QueryUpscaleVersions: null device");
        return 0;
    }

    // Pass 1 — capacity query (outputCount starts at 0, arrays null).
    ffxQueryDescGetVersions desc = {};
    desc.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    desc.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    desc.device = device;
    uint64_t count = 0;
    desc.outputCount = &count;

    ffxReturnCode_t ret = Functions().Query(nullptr, &desc.header);
    if (ret != FFX_API_RETURN_OK)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] ffxQuery(GetVersions,count) failed, ret=%u",
                          static_cast<unsigned>(ret));
        return 0;
    }
    if (count == 0)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] ffxQuery(GetVersions) reported 0 versions");
        return 0;
    }

    // Pass 2 — fill ids + names.
    std::vector<uint64_t> versionIds(static_cast<size_t>(count));
    std::vector<const char*> versionNames(static_cast<size_t>(count));
    desc.versionIds = versionIds.data();
    desc.versionNames = versionNames.data();

    ret = Functions().Query(nullptr, &desc.header);
    if (ret != FFX_API_RETURN_OK)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] ffxQuery(GetVersions,ids/names) failed, ret=%u",
                          static_cast<unsigned>(ret));
        return 0;
    }

    const uint64_t filled = count < versionNames.size()
                                ? count
                                : static_cast<uint64_t>(versionNames.size());
    outNames.reserve(static_cast<size_t>(filled));
    for (uint64_t i = 0; i < filled; ++i)
    {
        outNames.emplace_back(versionNames[i] != nullptr ? versionNames[i] : "");
    }
    return static_cast<int>(count);
}

bool FfxRuntime::InstallLogBridge()
{
    if (!IsLoaded())
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] InstallLogBridge: loader not loaded");
        return false;
    }

    // Primary: ffxConfigureDescGlobalDebug (type 7u, per-effect effectId).
    ffxConfigureDescGlobalDebug desc = {};
    desc.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG;
    desc.effectId = FFX_API_EFFECT_ID_GENERAL;
    desc.fpMessage = &FfxLogSink;
    desc.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;

    // NULL context targets FFX global state.
    ffxReturnCode_t ret = Functions().Configure(nullptr, &desc.header);
    if (ret == FFX_API_RETURN_OK)
    {
        catra::BackendLog(CATRA_LOG_INFO,
                          "[FFX] log bridge installed (GlobalDebug, level=WARNINGS)");
        return true;
    }

    // Fallback: the 2.3 loader answers FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE
    // for the 7u descriptor on a NULL context; the official reference
    // (fsrapirendermodule.cpp SetGlobalDebugCheckerMode) uses the legacy
    // ffxConfigureDescGlobalDebug1 (type 1u) instead.
    ffxConfigureDescGlobalDebug1 desc1 = {};
    desc1.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG1;
    desc1.fpMessage = &FfxLogSink;
    desc1.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;

    ret = Functions().Configure(nullptr, &desc1.header);
    if (ret != FFX_API_RETURN_OK)
    {
        catra::BackendLog(CATRA_LOG_WARN,
                          "[FFX] Configure(GlobalDebug log bridge) failed for "
                          "both desctypes (7u/1u), last ret=%u",
                          static_cast<unsigned>(ret));
        return false;
    }
    catra::BackendLog(CATRA_LOG_INFO,
                      "[FFX] log bridge installed (GlobalDebug1, level=WARNINGS)");
    return true;
}

} // namespace catra::ffx

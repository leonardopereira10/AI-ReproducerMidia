// ffx_load_smoke_test.cpp — FFX runtime smoke test (SPRINT_04, subtask 01).
// NOT shipped / NOT installed. Verifies end-to-end that the 8 FFX runtime
// DLLs load, that the 5 ffx exports resolve through catra::ffx::FfxRuntime,
// and that ffxQueryDescGetVersions reports an FFX 2.3.x upscale provider.
//
// Build: target catra-ffx-load-test in CMakeLists.txt (POST_BUILD copies the
//        8 DLLs next to the exe — they MUST be co-located).
// Usage: catra-ffx-load-test.exe [--cwd-elsewhere]
//   (no args)          scenario A: CWD = exe directory (default)
//   --cwd-elsewhere    scenario B: CWD switched to the Windows dir before any
//                      load — simulates the real app, where the FFX DLLs sit
//                      in runtimes/win-x64/native/ and CWD/search order do NOT
//                      cover them (validates FfxRuntime's full-path load).
// Exit codes: 0 PASS | 1 DLL load failure | 2 ffx export resolution failure
//             3 no DX12 adapter | 4 FFX version mismatch (expected 2.3.x)

#include "../ffx_runtime.h"

#include <libloaderapi.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// ffx_runtime.cpp links against the BackendLog shim normally provided by
// catra_gpu.cpp; the standalone test provides a stderr sink (same pattern as
// tools/interop_readback_test.cpp).
namespace catra {
void BackendLog(int level, const char* fmt, ...)
{
    (void)level;
    va_list args;
    va_start(args, fmt);
    vfprintf(stderr, fmt, args);
    va_end(args);
    fprintf(stderr, "\n");
}
} // namespace catra

namespace {

// List A1: the 8 FFX runtime DLLs, loaded explicitly first so a missing file
// produces a clear per-DLL verdict (LoadLibrary resolves them from the exe
// directory — the CMake POST_BUILD copy guarantees co-location).
constexpr const char* kDlls[] = {
    "amd_fidelityfx_loader_dx12.dll",
    "amd_fidelityfx_upscaler_dx12.dll",
    "amd_fidelityfx_framegeneration_dx12.dll",
    "amd_ags_x64.dll",
    "amd_acs_x64.dll",
    "D3D12Core.dll",
    "dxcompiler.dll",
    "dxil.dll",
};

// Local sink for the direct Configure smoke (step 5): proves the ffx message
// callback ABI end-to-end without depending on the runtime's internal sink.
void SmokeSink(uint32_t type, const wchar_t* message)
{
    printf("  [FFX sink] type=%u msg=%ls\n", static_cast<unsigned>(type),
           message != nullptr ? message : L"(null)");
}

} // namespace

int main(int argc, char** argv)
{
    printf("catra-ffx-load-test: FFX runtime smoke (SPRINT_04 ST-01)\n");

    // Scenario B (--cwd-elsewhere): move the CWD away from the DLL directory
    // BEFORE any load, simulating the real app (FFX DLLs live in
    // runtimes/win-x64/native/, never the process CWD). FfxRuntime::Load()
    // must still succeed because it loads by explicit full path from the
    // module directory.
    bool cwdElsewhere = false;
    for (int i = 1; i < argc; ++i)
    {
        if (std::strcmp(argv[i], "--cwd-elsewhere") == 0)
        {
            cwdElsewhere = true;
        }
    }
    if (cwdElsewhere)
    {
        wchar_t winDir[MAX_PATH] = {0};
        GetWindowsDirectoryW(winDir, MAX_PATH);
        if (SetCurrentDirectoryW(winDir) == 0)
        {
            fprintf(stderr, "FAIL SetCurrentDirectoryW err=%lu\n", GetLastError());
            return 1;
        }
        wchar_t cwd[MAX_PATH] = {0};
        GetCurrentDirectoryW(MAX_PATH, cwd);
        printf("scenario B: CWD moved to %ls (DLL dir NOT covered by CWD)\n", cwd);
    }
    else
    {
        printf("scenario A: CWD = default (exe/DLL directory)\n");
    }

    // --- Step 1: load the 8 runtime DLLs -----------------------------------
    std::vector<HMODULE> modules;
    modules.reserve(8);
    for (const char* name : kDlls)
    {
        const HMODULE mod = LoadLibraryA(name);
        if (mod == nullptr)
        {
            fprintf(stderr, "FAIL LoadLibraryA(%s) err=%lu\n", name,
                    GetLastError());
            return 1;
        }
        printf("OK   LoadLibraryA(%s)\n", name);
        modules.push_back(mod);
    }

    // --- Step 2: FfxRuntime::Load + resolve the 5 ffx exports --------------
    if (!catra::ffx::FfxRuntime::Load())
    {
        fprintf(stderr, "FAIL FfxRuntime::Load()\n");
        return 2;
    }
    const ffxFunctions& ffx = catra::ffx::FfxRuntime::Functions();
    if (ffx.CreateContext == nullptr || ffx.DestroyContext == nullptr ||
        ffx.Configure == nullptr || ffx.Query == nullptr ||
        ffx.Dispatch == nullptr)
    {
        fprintf(stderr, "FAIL ffx export resolution (NULL pointer among the 5)\n");
        return 2;
    }
    printf("OK   ffx exports resolved 5/5 (CreateContext/DestroyContext/"
           "Configure/Query/Dispatch)\n");

    // --- Step 3: transient DX12 device --------------------------------------
    ID3D12Device* device = nullptr;
    HRESULT hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0,
                                   IID_PPV_ARGS(&device));
    if (FAILED(hr))
    {
        hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0,
                               IID_PPV_ARGS(&device));
    }
    if (FAILED(hr))
    {
        fprintf(stderr,
                "FAIL D3D12CreateDevice hr=0x%08lX — no DX12 adapter present\n",
                static_cast<unsigned long>(hr));
        return 3;
    }
    printf("OK   transient DX12 device created\n");

    // --- Step 4: FFX upscale version query (expect 2.3.x) -------------------
    std::vector<std::string> names;
    const int count = catra::ffx::FfxRuntime::QueryUpscaleVersions(device, names);
    printf("ffxQueryDescGetVersions(UPSCALE): count=%d\n", count);
    bool has23 = false;
    for (const std::string& n : names)
    {
        printf("  version: %s\n", n.c_str());
        if (n.find("2.3") != std::string::npos)
        {
            has23 = true;
        }
    }
    if (count < 1 || !has23)
    {
        fprintf(stderr,
                "FAIL FFX version mismatch: expected >=1 upscale version "
                "containing \"2.3\" (got count=%d)\n", count);
        device->Release();
        return 4;
    }
    printf("OK   FFX 2.3.x upscale provider reported\n");

    // --- Step 5: log bridge + Configure smoke --------------------------------
    const bool bridge = catra::ffx::FfxRuntime::InstallLogBridge();
    printf("InstallLogBridge: %s\n", bridge ? "OK" : "FAILED (warn-logged)");

    ffxConfigureDescGlobalDebug smokeDesc = {};
    smokeDesc.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG;
    smokeDesc.effectId = FFX_API_EFFECT_ID_GENERAL;
    smokeDesc.fpMessage = &SmokeSink;
    smokeDesc.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS;
    const ffxReturnCode_t smokeRet =
        catra::ffx::FfxRuntime::Functions().Configure(nullptr, &smokeDesc.header);
    // Accept FFX_API_RETURN_OK or a non-fatal error code (already warn-logged
    // through the bridge); the smoke only requires no crash.
    printf("Configure smoke: ret=%u (%s)\n", static_cast<unsigned>(smokeRet),
           smokeRet == FFX_API_RETURN_OK ? "OK" : "non-fatal, logged");

    // --- Step 6: teardown ----------------------------------------------------
    for (auto it = modules.rbegin(); it != modules.rend(); ++it)
    {
        FreeLibrary(*it);
    }
    device->Release();
    catra::ffx::FfxRuntime::Unload();

    printf("PASS\n");
    return 0;
}

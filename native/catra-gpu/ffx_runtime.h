// ffx_runtime.h — runtime loader for the FidelityFX (FFX) API 2.x (SPRINT_04,
// subtask 01).
//
// This module is the runtime-loaded foundation of the FFX integration: it
// LoadLibrary's `amd_fidelityfx_loader_dx12.dll` and resolves the 5 FFX entry
// points (ffxCreateContext / ffxDestroyContext / ffxConfigure / ffxQuery /
// ffxDispatch) via GetProcAddress. There is NO build-time dependency on any
// FFX import lib — only the vendored headers (lib/FidelityFX-SDK-2.3.0) and
// the Windows system libs (d3d12/dxgi).
//
// The API is internal C++ (namespace catra::ffx). It is NOT exported through
// the flat C ABI of catra_gpu.h; stories 02/03/04 consume it directly from
// native code.
//
// THREADING: Load/Unload/IsLoaded are mutex-guarded and idempotent. The FFX
// log sink installed by InstallLogBridge() is invoked from FFX worker threads;
// it forwards to catra::BackendLog (defined in catra_gpu.cpp), which is itself
// thread-safe (g_logMutex). No C++ exception ever escapes this module.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

// GOTCHA (subtask 01, item 2a): the entire body of ffxLoadFunctions() inside
// ffx_api_loader.h is gated on
//     #if defined(_GAMING_XBOX) || defined(_WINDOWS) || defined(PLATFORM_WINDOWS)
// and MSVC does NOT define _WINDOWS on its own (only MSBuild x64 Windows
// configurations do). Without the define below the 5 function pointers are
// silently left NULL. Defining it here — before any FFX header is included —
// guarantees correct resolution for every translation unit that consumes this
// header. The same guard makes ffx_api_loader.h pull in <libloaderapi.h>.
#ifndef _WINDOWS
#define _WINDOWS
#endif

#include <windows.h>
#include <d3d12.h>

#include <ffx_api.h>
#include <ffx_api_loader.h>
// FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE (version queries / upscale contexts).
#include <ffx_upscale.h>

#include <string>
#include <vector>

namespace catra::ffx {

// Runtime loader + capability probe for the FidelityFX API. All members are
// static; the loader state is process-global (one HMODULE, one ffxFunctions).
class FfxRuntime
{
public:
    // Loads the loader via explicit full path (module dir +
    // "amd_fidelityfx_loader_dx12.dll") through LoadLibraryExW with
    // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR (fallback: default search order on
    // ancient Windows) + ffxLoadFunctions. The full path is mandatory: in the
    // shipped app the FFX DLLs sit next to catra-gpu.dll in
    // runtimes/win-x64/native/, which the process default search order does
    // NOT cover; the flag also resolves the loader's internal dependencies
    // from that same directory.
    // Idempotent: a second Load() while loaded is a no-op returning true.
    // Returns false when the DLL is absent or ANY of the 5 GetProcAddress
    // lookups yields NULL (the module is FreeLibrary'd and the state undone,
    // so a later Load() may retry after a deploy fix).
    static bool Load();

    // FreeLibrary on the loader module. Idempotent (no-op when not loaded).
    // Wired into catra_shutdown by story 02.
    static void Unload();

    static bool IsLoaded();

    // The resolved ffx entry points. Valid ONLY while IsLoaded() == true.
    static const ffxFunctions& Functions();

    // True when all 8 runtime DLLs (list A1) exist on disk in the directory
    // containing catra-gpu.dll itself (module-dir proxy via
    // GetModuleHandleExW FROM_ADDRESS, same trick as interp_rife.cpp).
    static bool ProbeDependencyDlls();

    // True when a DX12 adapter is present: D3D12CreateDevice on a transient
    // device (D3D_FEATURE_LEVEL_11_0, fallback 12_0). The device is released
    // before returning.
    static bool ProbeDx12Adapter();

    // Full availability gate: Load() && ProbeDependencyDlls() &&
    // ProbeDx12Adapter() && at least one upscale provider version reported by
    // ffxQueryDescGetVersions (0 versions == loader/runtime mismatch ->
    // unavailable). Never throws.
    static bool IsAvailable();

    // ffxQueryDescGetVersions (FFX_API_QUERY_DESC_TYPE_GET_VERSIONS) with
    // createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE and the given
    // DX12 device. Two passes: outputCount=0 -> capacity, then fill
    // versionIds/versionNames (pattern from fsrapirendermodule.cpp).
    // Returns the number of versions (>= 1) or 0 on any failure/mismatch
    // (fail-safe: warn-logged, no crash, no exception); outNames is cleared
    // first and holds the display names on success.
    static int QueryUpscaleVersions(ID3D12Device* device,
                                    std::vector<std::string>& outNames);

    // Routes FFX diagnostics into catra::BackendLog via
    // ffxConfigure(nullptr, ffxConfigureDescGlobalDebug) — a NULL context
    // targets global state. debugLevel = WARNINGS; ERROR/WARNING messages map
    // to CATRA_LOG_ERROR/CATRA_LOG_WARN. Returns false when not loaded or the
    // Configure call fails (warn-logged).
    static bool InstallLogBridge();
};

} // namespace catra::ffx

# CMakeLists.txt — catra-gpu native bridge (ST-12).
#
# Build via scripts/build-native.ps1, which supplies the vcpkg toolchain file.
# Produces catra-gpu.dll (+ import lib) and installs the DLL under
# runtimes/win-x64/native/ so the .NET native probe path finds it.

cmake_minimum_required(VERSION 3.25)

project(catra-gpu
    VERSION 0.1.0
    DESCRIPTION "CATRA GPU native bridge (FSR 4, RIFE, AMF, DX12)"
    LANGUAGES CXX)

# Default install prefix: relative to repo root so the BeforeBuild script and
# manual `cmake --install` both land the DLL in the .NET probe path without
# needing --prefix. Without this, CMake defaults to C:/Program Files (x86)/...
# which requires admin rights and silently fails from Visual Studio.
# Also overrides a stale cache entry pointing at Program Files.
if(CMAKE_INSTALL_PREFIX_INITIALIZED_TO_DEFAULT
   OR CMAKE_INSTALL_PREFIX MATCHES "[Pp]rogram [Ff]iles")
    set(CMAKE_INSTALL_PREFIX "${CMAKE_SOURCE_DIR}/install"
        CACHE PATH "Install path for catra-gpu DLL" FORCE)
endif()

# --- Language / toolchain ---------------------------------------------------
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# The bridge is Windows/DX12-only.
if(NOT WIN32)
    message(FATAL_ERROR "catra-gpu targets Windows (D3D11/D3D12) only.")
endif()

# Default to a Release build for single-config generators.
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# --- Dependencies (vcpkg) ---------------------------------------------------
# The vcpkg toolchain file (-DCMAKE_TOOLCHAIN_FILE) is passed by
# scripts/build-native.ps1. directx-headers provides the modern D3D12 headers.
# dxguid.lib comes from the Windows SDK (linked below as a system lib).
find_package(directx-headers CONFIG QUIET)

# --- FidelityFX SDK 2.3.0 (SPRINT_04 ST-01) ---------------------------------
# The FFX API is RUNTIME-LOADED (LoadLibrary/GetProcAddress via ffx_runtime.cpp):
# there is NO build-time dependency on any FFX import lib. The API headers are
# vendored (MIT) at lib/FidelityFX-SDK-2.3.0 and the 8 runtime DLLs come from
# the prebuilt FidelityFX Samples package (user-supplied, not committed).
set(CATRA_FFX_SDK_HEADERS "${CMAKE_SOURCE_DIR}/../../lib/FidelityFX-SDK-2.3.0"
    CACHE PATH "Vendored FidelityFX SDK 2.3.0 headers (MIT)")
set(CATRA_FFX_DLL_DIR "${CMAKE_SOURCE_DIR}/../../lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release"
    CACHE PATH "Prebuilt FFX runtime DLLs (samples package)")
# List A1: the 8 FFX runtime DLLs deployed next to catra-gpu.dll.
set(CATRA_FFX_DLLS amd_fidelityfx_loader_dx12.dll amd_fidelityfx_upscaler_dx12.dll
    amd_fidelityfx_framegeneration_dx12.dll amd_ags_x64.dll amd_acs_x64.dll
    D3D12Core.dll dxcompiler.dll dxil.dll)

if(NOT EXISTS "${CATRA_FFX_SDK_HEADERS}/api/include/ffx_api.h")
    message(FATAL_ERROR
        "catra-gpu: vendored FFX API headers not found at "
        "${CATRA_FFX_SDK_HEADERS}/api/include/ffx_api.h. ffx_runtime requires "
        "the vendored FidelityFX SDK 2.3.0 headers (lib/FidelityFX-SDK-2.3.0).")
endif()

# --- ONNX Runtime (RIFE frame interpolation, ST-13) -------------------------
# Resolved from vcpkg (onnxruntime-gpu) by default. A manual install can be
# pointed at via -DCATRA_ONNXRUNTIME_ROOT=<prefix> (expects include/ + lib/
# with onnxruntime.lib). When neither is found the bridge still builds; the
# RIFE backend then compiles to a CATRA_ERR_NOT_IMPL stub.
option(CATRA_WITH_RIFE "Build the RIFE interpolation backend (needs ONNX Runtime)" ON)

set(CATRA_ONNXRUNTIME_TARGET "")
if(CATRA_WITH_RIFE)
    find_package(onnxruntime-gpu CONFIG QUIET)
    if(TARGET onnxruntime::onnxruntime)
        set(CATRA_ONNXRUNTIME_TARGET onnxruntime::onnxruntime)
    elseif(DEFINED CATRA_ONNXRUNTIME_ROOT)
        get_filename_component(CATRA_ONNXRUNTIME_ROOT "${CATRA_ONNXRUNTIME_ROOT}" ABSOLUTE)

        # Support two layouts:
        #   A) Flat: <root>/include/ + <root>/lib/ + <root>/bin/
        #   B) NuGet: <root>/build/native/include/ + <root>/runtimes/win-x64/native/
        if(EXISTS "${CATRA_ONNXRUNTIME_ROOT}/build/native/include/onnxruntime_cxx_api.h")
            set(_ort_inc "${CATRA_ONNXRUNTIME_ROOT}/build/native/include")
            set(_ort_lib "${CATRA_ONNXRUNTIME_ROOT}/runtimes/win-x64/native/onnxruntime.lib")
            set(_ort_dll "${CATRA_ONNXRUNTIME_ROOT}/runtimes/win-x64/native/onnxruntime.dll")
        else()
            set(_ort_inc "${CATRA_ONNXRUNTIME_ROOT}/include")
            set(_ort_lib "${CATRA_ONNXRUNTIME_ROOT}/lib/onnxruntime.lib")
            set(_ort_dll "${CATRA_ONNXRUNTIME_ROOT}/bin/onnxruntime.dll")
        endif()

        if(NOT EXISTS "${_ort_lib}")
            message(WARNING "catra-gpu: ONNX Runtime lib not found at ${_ort_lib} -> RIFE disabled")
        elseif(NOT EXISTS "${_ort_dll}")
            message(WARNING "catra-gpu: ONNX Runtime DLL not found at ${_ort_dll} -> RIFE disabled")
        else()
            add_library(catra_onnxruntime_import SHARED IMPORTED)
            set_target_properties(catra_onnxruntime_import PROPERTIES
                INTERFACE_INCLUDE_DIRECTORIES "${_ort_inc}"
                IMPORTED_IMPLIB "${_ort_lib}"
                IMPORTED_LOCATION "${_ort_dll}")
            set(CATRA_ONNXRUNTIME_TARGET catra_onnxruntime_import)
        endif()
    endif()

    if(CATRA_ONNXRUNTIME_TARGET)
        message(STATUS "catra-gpu: ONNX Runtime found -> RIFE backend enabled")
    else()
        message(WARNING
            "catra-gpu: ONNX Runtime NOT found -> RIFE backend disabled "
            "(install onnxruntime-gpu via vcpkg or pass -DCATRA_ONNXRUNTIME_ROOT)")
    endif()
endif()

# --- PyTorch / ROCm (RIFE GPU acceleration, alternative to DirectML) --------
# PyTorch with ROCm support provides an alternative GPU execution path for RIFE
# inference. When both DirectML and ROCm are available, the runtime selects
# DirectML first (more stable on Windows), then ROCm, then CPU fallback.
# PyTorch is OPTIONAL; when absent the bridge uses DirectML/CPU only.
option(CATRA_USE_PYTORCH "Enable PyTorch/libtorch backend for RIFE (ROCm support)" ON)

set(CATRA_HAS_PYTORCH FALSE)
if(CATRA_USE_PYTORCH)
    # Try to find libtorch (PyTorch C++ API)
    # Detection strategy:
    #   1. Check CMake Torch_DIR variable (for standalone libtorch)
    #   2. Check LIBTORCH_DIR environment variable
    #   3. Try to find PyTorch in Python site-packages
    #   4. Check common installation paths
    
    set(TORCH_FOUND FALSE)
    set(TORCH_INCLUDE_DIRS "")
    set(TORCH_LIBRARIES "")
    
    # Method 1: Check if Torch_DIR is set (standalone libtorch)
    if(DEFINED Torch_DIR AND EXISTS "${Torch_DIR}/TorchConfig.cmake")
        find_package(Torch QUIET PATHS "${Torch_DIR}")
        if(TORCH_FOUND)
            message(STATUS "catra-gpu: Found libtorch (standalone) at ${Torch_DIR}")
        endif()
    endif()
    
    # Method 2: Check LIBTORCH_DIR environment variable
    if(NOT TORCH_FOUND AND DEFINED ENV{LIBTORCH_DIR})
        set(ENV_LIBTORCH_DIR "$ENV{LIBTORCH_DIR}")
        if(EXISTS "${ENV_LIBTORCH_DIR}/share/cmake/Torch/TorchConfig.cmake")
            find_package(Torch QUIET PATHS "${ENV_LIBTORCH_DIR}/share/cmake/Torch")
            if(TORCH_FOUND)
                message(STATUS "catra-gpu: Found libtorch at $ENV{LIBTORCH_DIR}")
            endif()
        endif()
    endif()
    
    # Method 3: Try to find PyTorch in Python site-packages
    if(NOT TORCH_FOUND)
        execute_process(
            COMMAND python -c "import torch; import os; print(os.path.dirname(torch.__file__))"
            OUTPUT_VARIABLE PYTORCH_PATH
            OUTPUT_STRIP_TRAILING_WHITESPACE
            ERROR_QUIET
            RESULT_VARIABLE PYTORCH_RESULT
        )
        
        if(PYTORCH_RESULT EQUAL 0 AND EXISTS "${PYTORCH_PATH}")
            message(STATUS "catra-gpu: Found PyTorch in Python at ${PYTORCH_PATH}")
            
            # Check if torch include directory exists AND has the required headers
            if(EXISTS "${PYTORCH_PATH}/include/torch/torch.h")
                set(TORCH_INCLUDE_DIRS "${PYTORCH_PATH}/include")
                
                # Find torch library (Windows: torch.lib, torch_cpu.lib, torch_cuda.lib)
                if(EXISTS "${PYTORCH_PATH}/lib/torch_cpu.lib")
                    set(TORCH_LIBRARIES "${PYTORCH_PATH}/lib/torch_cpu.lib")
                    if(EXISTS "${PYTORCH_PATH}/lib/torch_cuda.lib")
                        list(APPEND TORCH_LIBRARIES "${PYTORCH_PATH}/lib/torch_cuda.lib")
                    endif()
                    set(TORCH_FOUND TRUE)
                    message(STATUS "catra-gpu: Using PyTorch from Python installation")
                else()
                    message(STATUS "catra-gpu: PyTorch found but torch_cpu.lib missing -> disabled")
                endif()
            else()
                message(STATUS "catra-gpu: PyTorch found but headers incomplete (torch/torch.h missing)")
                message(STATUS "catra-gpu: pip-installed PyTorch does not include C++ API headers")
                message(STATUS "catra-gpu: Download libtorch from https://pytorch.org/get-started/locally/")
            endif()
        endif()
    endif()
    
    # Method 4: Check common paths
    if(NOT TORCH_FOUND)
        set(TORCH_SEARCH_PATHS
            "C:/pytorch/libtorch"
            "C:/libtorch"
            "$ENV{USERPROFILE}/libtorch"
        )
        
        foreach(SEARCH_PATH ${TORCH_SEARCH_PATHS})
            if(EXISTS "${SEARCH_PATH}/share/cmake/Torch/TorchConfig.cmake")
                find_package(Torch QUIET PATHS "${SEARCH_PATH}/share/cmake/Torch")
                if(TORCH_FOUND)
                    message(STATUS "catra-gpu: Found libtorch at ${SEARCH_PATH}")
                    break()
                endif()
            endif()
        endforeach()
    endif()
    
    if(TORCH_FOUND)
        set(CATRA_HAS_PYTORCH TRUE)
        message(STATUS "catra-gpu: PyTorch/libtorch found -> ROCm backend enabled")
        if(DEFINED TORCH_VERSION)
            message(STATUS "catra-gpu: PyTorch version: ${TORCH_VERSION}")
        endif()
    else()
        message(STATUS "catra-gpu: PyTorch/libtorch NOT found -> ROCm backend disabled")
        message(STATUS "catra-gpu: To enable:")
        message(STATUS "catra-gpu:   1. Install PyTorch: .\\scripts\\install-pytorch-rocm.ps1")
        message(STATUS "catra-gpu:   2. Or download libtorch from https://pytorch.org/get-started/locally/")
    endif()
endif()
# interp_rife.cpp requests the DirectML EP (via the C export
# OrtSessionOptionsAppendExecutionProvider_DML loaded through GetProcAddress)
# only when USE_DML is defined. The detection below checks for the presence of
# dml_provider_factory.h in the resolved ONNX headers (file-existence probe);
# a compile-based probe is not feasible because the header includes <DirectML.h>
# which requires the Microsoft.AI.DirectML SDK headers (not vendored).
# If the check fails, USE_DML stays OFF and RIFE falls back to the CPU EP:
# slow, but correct and always available (offline pipeline, RN-07).
# For a real DirectML build use the NuGet packages `onnxruntime-directml` /
# `Microsoft.AI.DirectML` (or a vcpkg overlay port built with --use_dml) and
# point -DCATRA_ONNXRUNTIME_ROOT at that install; the detection below will then
# find dml_provider_factory.h and enable the GPU path automatically.
option(CATRA_USE_DML "Request the ONNX Runtime DirectML EP for RIFE when the API is available" ON)

set(CATRA_HAS_DML FALSE)
if(CATRA_ONNXRUNTIME_TARGET AND CATRA_USE_DML)
    get_target_property(_ort_includes ${CATRA_ONNXRUNTIME_TARGET} INTERFACE_INCLUDE_DIRECTORIES)
    if(_ort_includes)
        # File-existence probe: the DirectML NuGet build ships
        # dml_provider_factory.h (declaring the C export
        # OrtSessionOptionsAppendExecutionProvider_DML). The C++ wrapper method
        # SessionOptions::AppendExecutionProvider_DML does NOT exist in this
        # package — interp_rife.cpp loads the C export via GetProcAddress.
        # A compile-based probe would fail because dml_provider_factory.h
        # includes <DirectML.h> which requires the Microsoft.AI.DirectML SDK
        # headers (not vendored). File existence is sufficient: the export
        # lives in onnxruntime.dll and is loaded dynamically at runtime.
        set(CATRA_DML_EP_AVAILABLE FALSE)
        foreach(_ort_inc_dir IN LISTS _ort_includes)
            if(EXISTS "${_ort_inc_dir}/dml_provider_factory.h")
                set(CATRA_DML_EP_AVAILABLE TRUE)
                break()
            endif()
        endforeach()
        if(CATRA_DML_EP_AVAILABLE)
            set(CATRA_HAS_DML TRUE)
            message(STATUS "catra-gpu: DirectML EP API detected -> RIFE GPU acceleration enabled")
        else()
            message(STATUS
                "catra-gpu: DirectML EP API NOT detected (onnxruntime built without DML?) "
                "-> RIFE CPU fallback")
        endif()
    else()
        message(STATUS
            "catra-gpu: ONNX target exposes no include dirs -> cannot probe DirectML, "
            "RIFE CPU fallback")
    endif()
elseif(CATRA_ONNXRUNTIME_TARGET AND NOT CATRA_USE_DML)
    message(STATUS "catra-gpu: CATRA_USE_DML=OFF -> RIFE CPU fallback")
endif()

# --- Target -----------------------------------------------------------------
add_library(catra-gpu SHARED
    catra_gpu.cpp
    d3d_interop.cpp
    encode_amf.cpp
    ffx_runtime.cpp
    interp_rife.cpp
    nv12_to_bgra_shader.cpp
    upscale_fsr1.cpp
    upscale_fsr4.cpp)

target_include_directories(catra-gpu PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

# Vendored FFX API headers (runtime-loaded only — NO FFX .lib is linked).
# The layout of lib/FidelityFX-SDK-2.3.0 mirrors the SDK, so the headers'
# internal relative includes (e.g. ffx_upscale.h -> ../../api/include/ffx_api.h)
# resolve as-is; do not restructure that directory.
target_include_directories(catra-gpu PRIVATE
    "${CATRA_FFX_SDK_HEADERS}/api/include"
    "${CATRA_FFX_SDK_HEADERS}/upscalers/include"
    "${CATRA_FFX_SDK_HEADERS}/framegeneration/include")

# Link PyTorch/libtorch if found (ROCm backend)
if(CATRA_HAS_PYTORCH)
    target_compile_definitions(catra-gpu PRIVATE CATRA_HAS_PYTORCH=1)
    
    # Include PyTorch headers
    if(TORCH_INCLUDE_DIRS)
        target_include_directories(catra-gpu PRIVATE ${TORCH_INCLUDE_DIRS})
    endif()
    
    # Link PyTorch libraries
    # When using find_package(Torch), TORCH_LIBRARIES is set automatically
    # When using Python pip install, we manually set the library paths
    if(TORCH_LIBRARIES)
        target_link_libraries(catra-gpu PRIVATE ${TORCH_LIBRARIES})
    elseif(TORCH_FOUND)
        # find_package(Torch) was successful, use its targets
        target_link_libraries(catra-gpu PRIVATE torch torch_cpu)
        if(TORCH_CUDA_FOUND)
            target_link_libraries(catra-gpu PRIVATE torch_cuda)
        endif()
    endif()
    
    # Copy PyTorch DLLs to output directory (Windows only)
    if(WIN32 AND DEFINED PYTORCH_PATH)
        file(GLOB PYTORCH_DLLS "${PYTORCH_PATH}/lib/*.dll")
        install(FILES ${PYTORCH_DLLS} DESTINATION runtimes/win-x64/native OPTIONAL)
    endif()
endif()

target_compile_definitions(catra-gpu PRIVATE CATRA_GPU_BUILDING)

if(MSVC)
    # High warning bar, standards-conformant, UTF-8 sources. Warnings are
    # promoted to errors to keep the native build as strict as the C# one.
    target_compile_options(catra-gpu PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
    target_compile_definitions(catra-gpu PRIVATE _CRT_SECURE_NO_WARNINGS)

    # --- Architecture optimizations (AMD RDNA 4 / RX 9070 XT) ---------------
    # Enable AVX2 instruction set for vectorized operations. RDNA 4 GPUs support
    # AVX2 via the CPU they run on (most modern AMD CPUs support AVX2).
    # This accelerates CPU-side compute (e.g., NV12->BGRA conversion, color math).
    target_compile_options(catra-gpu PRIVATE /arch:AVX2)

    # Release-specific optimizations: maximum speed, whole-program optimization.
    # /O2 = maximize speed, /GL = whole-program optimization (link-time code gen).
    # These are applied per-configuration so Debug builds remain debuggable.
    target_compile_options(catra-gpu PRIVATE $<$<CONFIG:Release>:/O2 /GL>)
    # Link-time optimization for Release builds: enables cross-module inlining.
    set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "${CMAKE_SHARED_LINKER_FLAGS_RELEASE} /LTCG")
endif()

# System import libs ship with the Windows SDK. d3dcompiler provides
# D3DCompile, which upscale_fsr1.cpp uses to compile the embedded EASU HLSL to
# DXBC at runtime (no on-disk shader blob, no dxc dependency).
target_link_libraries(catra-gpu PRIVATE
    d3d12
    dxgi
    d3d11
    dxguid
    d3dcompiler)

# --- Diagnostic tool (NOT installed/shipped) -------------------------------
# Standalone readback test: verifies the D3D11 -> D3D12 pooled share actually
# transfers pixel data on the current driver (AMD RDNA 4 green-frame issue).
add_executable(catra-interop-test
    tools/interop_readback_test.cpp
    d3d_interop.cpp
    encode_amf.cpp)
target_include_directories(catra-interop-test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
if(MSVC)
    target_compile_options(catra-interop-test PRIVATE /W3 /utf-8 /EHsc)
    target_compile_definitions(catra-interop-test PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()
target_link_libraries(catra-interop-test PRIVATE d3d12 dxgi d3d11 dxguid d3dcompiler)
if(TARGET Microsoft::DirectX-Headers)
    target_link_libraries(catra-interop-test PRIVATE Microsoft::DirectX-Headers)
endif()
# The RIFE stage of the diagnostic tool needs ONNX Runtime (+DirectML) just
# like the main DLL. The --fsr4-smoke stage (SPRINT_04 subtask 02) links the
# runtime-loaded FSR 4/3.1 backend + ffx_runtime (headers-only, no FFX lib).
target_sources(catra-interop-test PRIVATE
    interp_rife.cpp upscale_fsr1.cpp upscale_fsr4.cpp ffx_runtime.cpp)
target_include_directories(catra-interop-test PRIVATE
    "${CATRA_FFX_SDK_HEADERS}/api/include"
    "${CATRA_FFX_SDK_HEADERS}/upscalers/include")
if(CATRA_ONNXRUNTIME_TARGET)
    target_compile_definitions(catra-interop-test PRIVATE CATRA_HAS_ONNXRUNTIME=1)
    target_link_libraries(catra-interop-test PRIVATE ${CATRA_ONNXRUNTIME_TARGET})
endif()
if(CATRA_HAS_DML)
    target_compile_definitions(catra-interop-test PRIVATE USE_DML=1)
endif()
# Co-locate the 8 FFX runtime DLLs with the test exe so --fsr4-smoke can load
# them (same pattern as catra-ffx-load-test below).
if(EXISTS "${CATRA_FFX_DLL_DIR}")
    foreach(_dll IN LISTS CATRA_FFX_DLLS)
        add_custom_command(TARGET catra-interop-test POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "${CATRA_FFX_DLL_DIR}/${_dll}" "$<TARGET_FILE_DIR:catra-interop-test>/${_dll}"
            COMMAND_EXPAND_LISTS)
    endforeach()
endif()

# --- FFX runtime smoke test (SPRINT_04 ST-01, NOT installed/shipped) --------
# Loads the 8 FFX runtime DLLs (list A1), resolves the 5 ffx exports via
# catra::ffx::FfxRuntime and validates ffxQueryDescGetVersions reports an
# FFX 2.3.x upscale provider. Exit codes: 1 DLL load, 2 export resolution,
# 3 no DX12 adapter, 4 version mismatch. The POST_BUILD copies co-locate the
# runtime DLLs with the exe (LoadLibrary search order resolves them there).
add_executable(catra-ffx-load-test
    tools/ffx_load_smoke_test.cpp
    ffx_runtime.cpp)
target_include_directories(catra-ffx-load-test PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    "${CATRA_FFX_SDK_HEADERS}/api/include"
    "${CATRA_FFX_SDK_HEADERS}/upscalers/include"
    "${CATRA_FFX_SDK_HEADERS}/framegeneration/include")
if(MSVC)
    target_compile_options(catra-ffx-load-test PRIVATE /W3 /utf-8 /EHsc)
    target_compile_definitions(catra-ffx-load-test PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()
target_link_libraries(catra-ffx-load-test PRIVATE d3d12 dxgi dxguid)

if(EXISTS "${CATRA_FFX_DLL_DIR}")
    foreach(_dll IN LISTS CATRA_FFX_DLLS)
        add_custom_command(TARGET catra-ffx-load-test POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                    "${CATRA_FFX_DLL_DIR}/${_dll}" "$<TARGET_FILE_DIR:catra-ffx-load-test>/${_dll}"
            COMMAND_EXPAND_LISTS)
    endforeach()
else()
    message(WARNING
        "catra-gpu: FFX prebuilt DLL dir not found at ${CATRA_FFX_DLL_DIR} -> "
        "catra-ffx-load-test builds but cannot run (runtime DLLs missing).")
endif()

# Optional vcpkg-provided packages (linked when the toolchain resolved them).
if(TARGET Microsoft::DirectX-Headers)
    target_link_libraries(catra-gpu PRIVATE Microsoft::DirectX-Headers)
endif()

# ONNX Runtime (RIFE). The define switches interp_rife.cpp from its stub to the
# real ONNX/DirectML implementation.
if(CATRA_ONNXRUNTIME_TARGET)
    target_compile_definitions(catra-gpu PRIVATE CATRA_HAS_ONNXRUNTIME=1)
    target_link_libraries(catra-gpu PRIVATE ${CATRA_ONNXRUNTIME_TARGET})
endif()

# USE_DML switches on the DirectML EP request inside interp_rife.cpp. Defined
# only when the feature probe above confirmed the API exists; otherwise the
# backend compiles the CPU-only path (the #if defined(USE_DML) block is
# intentionally inert, not dead code by accident).
if(CATRA_HAS_DML)
    target_compile_definitions(catra-gpu PRIVATE USE_DML=1)
endif()

# --- FSR 4 / FidelityFX (SPRINT_04 subtask 02) ------------------------------
# The old build-time compile gate (CATRA_WITH_FSR4 / CATRA_FSR_SDK_ROOT /
# CATRA_FSR_SDK_LIB + the CATRA_HAS_FSR4 define) was REMOVED: the FSR 4/3.1
# backend (upscale_fsr4.cpp) is now ALWAYS compiled and runtime-loaded via
# ffx_runtime.cpp (LoadLibrary + GetProcAddress; headers-only, no link-time
# dependency). The vendored FFX API headers are added to the include path in
# the "FidelityFX SDK 2.3.0" section above; the 8 runtime DLLs are deployed
# next to catra-gpu.dll by the install rules at the bottom of this file.

# --- AMF H.265 encode (ST-16) ----------------------------------------------
# AMF (AMD Media Framework) is HEADERS-ONLY on GPUOpen and is NEVER vendored or
# downloaded by this repo (lib/amf/ is gitignored). encode_amf.cpp only lights
# up when the integrator supplies the SDK via -DCATRA_AMF_ROOT=<AMF repo root>
# (the directory containing AMF/public/include/...; clone
# https://github.com/GPUOpen-LibrariesAndSDKs/AMF). The AMF *runtime*
# (amfrt64.dll) ships with the AMD Adrenalin driver and is loaded dynamically at
# runtime, so there is NO AMF import lib to link.
#
# Expected layout (see scripts/build-native.ps1):
#   <CATRA_AMF_ROOT>/AMF/public/include/core/Factory.h
#   <CATRA_AMF_ROOT>/AMF/public/include/core/Context.h
#   <CATRA_AMF_ROOT>/AMF/public/include/components/VideoEncoderHEVC.h
#
# When the headers are absent, CATRA_HAS_AMF stays undefined: encode_amf.cpp
# compiles to an inert stub and catra_encode_* return CATRA_ERR_NOT_IMPL
# gracefully (no crash). The repo root is added to the include path so the
# backend can use the canonical <AMF/public/include/...> include form.
option(CATRA_WITH_AMF "Build the AMF H.265 encode backend (needs CATRA_AMF_ROOT)" ON)

set(CATRA_HAS_AMF FALSE)
if(CATRA_WITH_AMF AND DEFINED CATRA_AMF_ROOT)
    set(_amf_factory_hdr "${CATRA_AMF_ROOT}/AMF/public/include/core/Factory.h")
    if(EXISTS "${_amf_factory_hdr}")
        set(CATRA_HAS_AMF TRUE)
        target_include_directories(catra-gpu PRIVATE "${CATRA_AMF_ROOT}")
        target_compile_definitions(catra-gpu PRIVATE CATRA_HAS_AMF=1)
        # The diagnostic tool exercises the AMF encode path too.
        target_include_directories(catra-interop-test PRIVATE "${CATRA_AMF_ROOT}")
        target_compile_definitions(catra-interop-test PRIVATE CATRA_HAS_AMF=1)
        message(STATUS "catra-gpu: AMF SDK found at ${CATRA_AMF_ROOT} -> AMF H.265 encode enabled")
    else()
        message(WARNING
            "catra-gpu: CATRA_AMF_ROOT='${CATRA_AMF_ROOT}' but ${_amf_factory_hdr} "
            "not found -> AMF encode disabled (CATRA_ERR_NOT_IMPL). Point -DCATRA_AMF_ROOT "
            "at the AMF repo root (the dir containing AMF/public/include/).")
    endif()
elseif(CATRA_WITH_AMF)
    message(STATUS
        "catra-gpu: CATRA_AMF_ROOT not set -> AMF H.265 encode disabled "
        "(headers-only GPUOpen SDK; runtime comes from the AMD driver). "
        "Pass -DCATRA_AMF_ROOT=<AMF repo root> to enable.")
endif()

# --- Install ----------------------------------------------------------------
# RUNTIME -> the DLL; LIBRARY/ARCHIVE -> import lib for potential native tests.
# The DLL lands in runtimes/win-x64/native/, matching the .NET probe layout and
# the CATRA.App.csproj copy rule.
install(TARGETS catra-gpu
    RUNTIME DESTINATION runtimes/win-x64/native
    LIBRARY DESTINATION runtimes/win-x64/native
    ARCHIVE DESTINATION lib)

# Install PDB (debug symbols) alongside the DLL so Visual Studio can
# debug into the native bridge code. Only present in Debug builds.
if(MSVC)
    install(FILES "$<TARGET_PDB_FILE:catra-gpu>"
        DESTINATION runtimes/win-x64/native
        OPTIONAL)
endif()

# Deploy the ONNX Runtime (+ DirectML) DLLs next to catra-gpu.dll so the loader
# finds them at runtime. Best-effort: skipped silently when the location cannot
# be resolved (vcpkg also copies runtime deps into the toolchain bin dir).
if(CATRA_ONNXRUNTIME_TARGET)
    get_target_property(_ort_dll ${CATRA_ONNXRUNTIME_TARGET} IMPORTED_LOCATION)
    if(_ort_dll AND EXISTS "${_ort_dll}")
        get_filename_component(_ort_bin "${_ort_dll}" DIRECTORY)
        file(GLOB _ort_runtime_dlls "${_ort_bin}/*.dll")
        install(FILES ${_ort_runtime_dlls} DESTINATION runtimes/win-x64/native OPTIONAL)
    endif()
endif()

# Deploy the 8 FFX runtime DLLs (list A1) + the MIT license next to
# catra-gpu.dll (SPRINT_04 ST-01; plan adjustments A1 + A7). The loader
# resolves the sibling DLLs from the module directory at runtime. OPTIONAL
# keeps `cmake --install` green on clones without the prebuilt samples package.
foreach(_dll IN LISTS CATRA_FFX_DLLS)
    install(FILES "${CATRA_FFX_DLL_DIR}/${_dll}"
            DESTINATION runtimes/win-x64/native OPTIONAL)
endforeach()
install(FILES "${CATRA_FFX_SDK_HEADERS}/LICENSE"
        DESTINATION runtimes/win-x64/native RENAME FFX-LICENSE.txt OPTIONAL)

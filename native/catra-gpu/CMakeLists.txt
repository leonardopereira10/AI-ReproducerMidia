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
# scripts/build-native.ps1. directx-headers provides the modern D3D12 headers;
# dxguid supplies the legacy GUID import lib.
find_package(directx-headers CONFIG QUIET)
find_package(dxguid CONFIG QUIET)

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
        add_library(catra_onnxruntime_import SHARED IMPORTED)
        set_target_properties(catra_onnxruntime_import PROPERTIES
            INTERFACE_INCLUDE_DIRECTORIES "${CATRA_ONNXRUNTIME_ROOT}/include"
            IMPORTED_IMPLIB "${CATRA_ONNXRUNTIME_ROOT}/lib/onnxruntime.lib"
            IMPORTED_LOCATION "${CATRA_ONNXRUNTIME_ROOT}/bin/onnxruntime.dll")
        set(CATRA_ONNXRUNTIME_TARGET catra_onnxruntime_import)
    endif()

    if(CATRA_ONNXRUNTIME_TARGET)
        message(STATUS "catra-gpu: ONNX Runtime found -> RIFE backend enabled")
    else()
        message(WARNING
            "catra-gpu: ONNX Runtime NOT found -> RIFE backend disabled "
            "(install onnxruntime-gpu via vcpkg or pass -DCATRA_ONNXRUNTIME_ROOT)")
    endif()
endif()

# --- DirectML execution provider (RIFE GPU acceleration, ST-13) -------------
# interp_rife.cpp requests the DirectML EP (AppendExecutionProvider_DML) only
# when USE_DML is defined. IMPORTANT: the vcpkg `onnxruntime-gpu` port is
# commonly built with the CUDA provider and does NOT expose the DirectML API,
# so we feature-detect the declaration in the resolved ONNX headers instead of
# assuming it. If the check fails, USE_DML stays OFF and RIFE falls back to the
# CPU EP: slow, but correct and always available (offline pipeline, RN-07).
# For a real DirectML build use the NuGet packages `onnxruntime-directml` /
# `Microsoft.AI.DirectML` (or a vcpkg overlay port built with --use_dml) and
# point -DCATRA_ONNXRUNTIME_ROOT at that install; the detection below will then
# find AppendExecutionProvider_DML and enable the GPU path automatically.
option(CATRA_USE_DML "Request the ONNX Runtime DirectML EP for RIFE when the API is available" ON)

set(CATRA_HAS_DML FALSE)
if(CATRA_ONNXRUNTIME_TARGET AND CATRA_USE_DML)
    get_target_property(_ort_includes ${CATRA_ONNXRUNTIME_TARGET} INTERFACE_INCLUDE_DIRECTORIES)
    if(_ort_includes)
        include(CheckCXXSourceCompiles)
        # Compile-only probe (no link): we only need to know whether the header
        # declares the DirectML EP entry point, not whether it links.
        set(_catra_backup_try_compile_type "${CMAKE_TRY_COMPILE_TARGET_TYPE}")
        set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
        set(CMAKE_REQUIRED_INCLUDES "${_ort_includes}")
        check_cxx_source_compiles("
            #include <onnxruntime_cxx_api.h>
            #include <type_traits>
            #include <utility>
            template <typename T> static auto catra_has_dml(int)
                -> decltype(std::declval<T>().AppendExecutionProvider_DML(0), std::true_type{});
            template <typename> static std::false_type catra_has_dml(...);
            static_assert(decltype(catra_has_dml<Ort::SessionOptions>(0))::value,
                          \"ONNX Runtime headers lack the DirectML EP API\");
            int main() { return 0; }
        " CATRA_DML_EP_AVAILABLE)
        set(CMAKE_TRY_COMPILE_TARGET_TYPE "${_catra_backup_try_compile_type}")
        unset(CMAKE_REQUIRED_INCLUDES)
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
    interp_rife.cpp)

target_include_directories(catra-gpu PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

target_compile_definitions(catra-gpu PRIVATE CATRA_GPU_BUILDING)

if(MSVC)
    # High warning bar, standards-conformant, UTF-8 sources. Warnings are
    # promoted to errors to keep the native build as strict as the C# one.
    target_compile_options(catra-gpu PRIVATE /W4 /WX /permissive- /utf-8 /EHsc)
endif()

# System import libs ship with the Windows SDK.
target_link_libraries(catra-gpu PRIVATE
    d3d12
    dxgi
    d3d11
    dxguid)

# Optional vcpkg-provided packages (linked when the toolchain resolved them).
if(TARGET Microsoft::DirectX-Headers)
    target_link_libraries(catra-gpu PRIVATE Microsoft::DirectX-Headers)
endif()
if(TARGET Microsoft::DXGUID)
    target_link_libraries(catra-gpu PRIVATE Microsoft::DXGUID)
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

# --- Install ----------------------------------------------------------------
# RUNTIME -> the DLL; LIBRARY/ARCHIVE -> import lib for potential native tests.
# The DLL lands in runtimes/win-x64/native/, matching the .NET probe layout and
# the CATRA.App.csproj copy rule.
install(TARGETS catra-gpu
    RUNTIME DESTINATION runtimes/win-x64/native
    LIBRARY DESTINATION runtimes/win-x64/native
    ARCHIVE DESTINATION lib)

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

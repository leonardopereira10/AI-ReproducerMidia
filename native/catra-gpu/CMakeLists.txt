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

# --- Target -----------------------------------------------------------------
add_library(catra-gpu SHARED
    catra_gpu.cpp
    d3d_interop.cpp)

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

# --- Install ----------------------------------------------------------------
# RUNTIME -> the DLL; LIBRARY/ARCHIVE -> import lib for potential native tests.
# The DLL lands in runtimes/win-x64/native/, matching the .NET probe layout and
# the CATRA.App.csproj copy rule.
install(TARGETS catra-gpu
    RUNTIME DESTINATION runtimes/win-x64/native
    LIBRARY DESTINATION runtimes/win-x64/native
    ARCHIVE DESTINATION lib)

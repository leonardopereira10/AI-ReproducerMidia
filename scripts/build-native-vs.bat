@echo off
REM build-native-vs.bat — Build native code from Visual Studio (BeforeBuild target).
REM Called by CATRA.App.csproj BeforeBuild target when building from Visual Studio
REM (Ctrl+B / F6). Assumes the MSVC environment is already configured by VS.
REM
REM Guarantees:
REM   * DLLs are ALWAYS installed to native/catra-gpu/install/ (project-local).
REM     CMAKE_INSTALL_PREFIX is forced on every cmake invocation so a stale or
REM     missing cache can NEVER default to C:\Program Files.
REM   * vcpkg manifest-mode toolchain is passed explicitly, so the build works
REM     even on a clean clone (no prior build-native.ps1 run required).
REM   * Install step always runs so the .NET copy rule sees a fresh DLL.
REM
REM PyTorch/ROCm Support:
REM If PyTorch with ROCm is installed, the build automatically detects it and
REM enables ROCm as an additional RIFE backend (priority: DirectML -> ROCm -> CPU).
REM
REM Arguments:
REM   %1 = Configuration (Debug|Release). Defaults to Release when omitted.
REM        Passed by CATRA.App.csproj via $(Configuration).

setlocal enabledelayedexpansion

REM Get the repo root (this script is in scripts/)
set "REPO_ROOT=%~dp0.."
pushd "%REPO_ROOT%"

REM --- Configuration (Debug|Release) ----------------------------------------
set "CONFIG=%~1"
if /i "!CONFIG!"=="Debug" (
    set "CONFIG=Debug"
) else (
    set "CONFIG=Release"
)
echo [NativeBuild] Configuration: !CONFIG!

REM --- Pre-flight checks ---------------------------------------------------
where cmake >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [NativeBuild] ERROR: cmake not found on PATH
    popd
    exit /b 1
)

if not exist "native\catra-gpu\CMakeLists.txt" (
    echo [NativeBuild] ERROR: native\catra-gpu\CMakeLists.txt not found
    popd
    exit /b 1
)

echo [NativeBuild] Building catra-gpu native bridge...

REM --- Resolve absolute paths -----------------------------------------------
REM cmake -B/-S accept relative paths, but CMAKE_INSTALL_PREFIX must be
REM absolute to avoid any "Program Files" default ambiguity.
set "NATIVE_DIR=native\catra-gpu"
set "BUILD_DIR=native\catra-gpu\build"
set "INSTALL_DIR_ABSOLUTE=%CD%\native\catra-gpu\install"

REM --- vcpkg toolchain (manifest mode) --------------------------------------
REM VCPKG_ROOT may come from the environment or be set manually.
REM Auto-detect common vcpkg locations when the env var is not set.
REM When available, pass the toolchain file so directx-headers and any other
REM vcpkg dependencies are resolved automatically.
set "VCPKG_ARGS="
if not defined VCPKG_ROOT (
    REM Try common locations relative to the project
    if exist "%REPO_ROOT%\..\vcpkg\scripts\buildsystems\vcpkg.cmake" (
        set "VCPKG_ROOT=%REPO_ROOT%\..\vcpkg"
    ) else if exist "C:\Projetos\vcpkg\scripts\buildsystems\vcpkg.cmake" (
        set "VCPKG_ROOT=C:\Projetos\vcpkg"
    ) else if exist "C:\vcpkg\scripts\buildsystems\vcpkg.cmake" (
        set "VCPKG_ROOT=C:\vcpkg"
    )
)
if defined VCPKG_ROOT (
    if exist "%VCPKG_ROOT%\scripts\buildsystems\vcpkg.cmake" (
        set "VCPKG_ARGS=-DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%\scripts\buildsystems\vcpkg.cmake -DVCPKG_TARGET_TRIPLET=x64-windows"
        echo [NativeBuild] vcpkg toolchain: %VCPKG_ROOT%
    )
)

REM --- Detect PyTorch/libtorch for ROCm backend -----------------------------
set "TORCH_CMAKE_ARGS=-DCATRA_USE_PYTORCH=OFF"
python -c "import torch; import os; print(os.path.dirname(torch.__file__))" > "%TEMP%\torch_path.txt" 2>nul
if %ERRORLEVEL% equ 0 (
    set /p TORCH_DIR=<"%TEMP%\torch_path.txt"
    del "%TEMP%\torch_path.txt"
    echo [NativeBuild] PyTorch detected at: !TORCH_DIR!
    set "TORCH_CMAKE_ARGS=-DTorch_DIR=!TORCH_DIR!\share\cmake -DCATRA_USE_PYTORCH=ON"
) else (
    if exist "%TEMP%\torch_path.txt" del "%TEMP%\torch_path.txt"
    echo [NativeBuild] PyTorch not detected, using DirectML/CPU backends
)

REM --- Detect AMF SDK -------------------------------------------------------
set "AMF_ARGS="
if exist "lib\amf\AMF\public\include\core\Factory.h" (
    set "AMF_ARGS=-DCATRA_AMF_ROOT=%CD%\lib\amf"
    echo [NativeBuild] AMF SDK found at lib\amf
)

REM --- Detect ONNX Runtime --------------------------------------------------
set "ONNX_ARGS="
if exist "lib\onnxruntime" (
    for /d %%D in ("lib\onnxruntime\microsoft.ml.onnxruntime.directml\*") do (
        set "ONNX_ARGS=-DCATRA_ONNXRUNTIME_ROOT=%CD%\%%D"
        echo [NativeBuild] ONNX Runtime found at %%D
    )
)

REM --- Configure (CMAKE_INSTALL_PREFIX forced to project-local path) ---------
echo [NativeBuild] cmake configure...
cmake -B "%BUILD_DIR%" -S "%NATIVE_DIR%" ^
    -DCMAKE_BUILD_TYPE=!CONFIG! ^
    -DCMAKE_INSTALL_PREFIX="%INSTALL_DIR_ABSOLUTE%" ^
    !VCPKG_ARGS! ^
    !TORCH_CMAKE_ARGS! ^
    !AMF_ARGS! ^
    !ONNX_ARGS!

if %ERRORLEVEL% neq 0 (
    echo [NativeBuild] ERROR: cmake configure failed
    popd
    exit /b 1
)

REM --- Build -----------------------------------------------------------------
echo [NativeBuild] cmake build...
cmake --build "%BUILD_DIR%" --config !CONFIG!
if %ERRORLEVEL% neq 0 (
    echo [NativeBuild] ERROR: Build failed
    popd
    exit /b 1
)

REM --- Install (always runs, guarantees project-local output) ----------------
REM Clean the install dir before installing so timestamps are refreshed and
REM stale Debug/Release artifacts (e.g. leftover PDB from Debug in a Release
REM build) are removed. This ensures PreserveNewest in the .csproj copies
REM the correct DLL.
echo [NativeBuild] Cleaning install dir...
if exist "%INSTALL_DIR_ABSOLUTE%" (
    rmdir /s /q "%INSTALL_DIR_ABSOLUTE%" 2>nul
)
echo [NativeBuild] cmake install to %INSTALL_DIR_ABSOLUTE%...
cmake --install "%BUILD_DIR%" --prefix "%INSTALL_DIR_ABSOLUTE%" --config !CONFIG!
if %ERRORLEVEL% neq 0 (
    echo [NativeBuild] ERROR: Install failed
    popd
    exit /b 1
)

REM --- Verify ----------------------------------------------------------------
if exist "%INSTALL_DIR_ABSOLUTE%\runtimes\win-x64\native\catra-gpu.dll" (
    echo [NativeBuild] SUCCESS: catra-gpu.dll installed to project folder
    echo [NativeBuild] Output: %INSTALL_DIR_ABSOLUTE%\runtimes\win-x64\native\
) else (
    echo [NativeBuild] WARNING: catra-gpu.dll not found after install
)

popd
endlocal
exit /b 0

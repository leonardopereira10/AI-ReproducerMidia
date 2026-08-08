@echo off
echo === Setting up MSVC Build Tools ===
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if %ERRORLEVEL% neq 0 (
    echo FATAL: vcvars64.bat failed
    exit /b 1
)
echo === MSVC ready ===
cl.exe 2>&1 | findstr /i "version"
echo === Running build-native.ps1 ===
cd /d "C:\Projetos\Reprodutor_CATRA"
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1 -VcpkgRoot "C:\Projetos\vcpkg" -OnnxRuntimeRoot "lib\onnxruntime\microsoft.ml.onnxruntime.directml\1.18.1" -AmfRoot "lib\amf"
echo === BUILD EXIT CODE: %ERRORLEVEL% ===

# build-native.ps1 — build the catra-gpu native bridge (ST-12).
#
# Prerequisites (one-time, NOT installed by this script):
#   * Visual Studio 2022 with the "Desktop development with C++" workload
#   * Windows SDK 10.0.22621+ (D3D12 headers)
#   * CMake 3.25+ on PATH
#   * vcpkg: git clone https://github.com/microsoft/vcpkg; .\bootstrap-vcpkg.bat
#     then set $env:VCPKG_ROOT to the clone root (or pass -VcpkgRoot).
#   * ONNX Runtime DirectML (optional, for RIFE): NuGet package
#     Microsoft.ML.OnnxRuntime.DirectML -> pass -OnnxRuntimeRoot <prefix>.
#     NOT installed via vcpkg (the vcpkg port pulls CUDA, not DirectML).
#
# Usage:
#   pwsh ./scripts/build-native.ps1 [-Configuration Release] [-VcpkgRoot <path>]
#
# Output:
#   native/catra-gpu/build/...                      (build tree)
#   native/catra-gpu/install/runtimes/win-x64/native/catra-gpu.dll
#       (+ onnxruntime.dll / DirectML.dll when the ONNX Runtime package is
#        resolved, deployed by the CMake install step)
#   lib/rife/rife_v4.onnx                            (downloaded on demand)
# The CATRA.App.csproj copies that installed DLL into the app output when it
# exists, so `dotnet build` picks it up automatically afterwards.

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$VcpkgRoot = $env:VCPKG_ROOT,

    # RIFE v4 arbitrary-timestep ONNX model. Override to point at a specific
    # Practical-RIFE export. NOT downloaded when the file already exists.
    [string]$RifeModelUrl = 'https://github.com/hzwer/Practical-RIFE/releases/latest/download/rife-v4.onnx',

    # FSR 4 / FidelityFX SDK root (ST-14). OPTIONAL and license-gated: this
    # script NEVER downloads it. When omitted, FSR 4 is disabled and the bridge
    # falls back to the self-contained FSR 1 (EASU) upscaler. To enable FSR 4:
    #   1. Accept the GPUOpen license and clone the FidelityFX SDK:
    #        git clone https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK lib/fsr-sdk
    #      (lib/fsr-sdk/ is gitignored.)
    #   2. Build the SDK's ffx_api backend project (ffx_api_backend.lib).
    #   3. Pass -FsrSdkRoot lib/fsr-sdk -FsrSdkLib <...>/ffx_api_backend.lib
    # CMake then defines CATRA_HAS_FSR4 and lights up the FSR 4 backend.
    [string]$FsrSdkRoot = $env:CATRA_FSR_SDK_ROOT,
    [string]$FsrSdkLib  = $env:CATRA_FSR_SDK_LIB,

    # AMF H.265 encode SDK root (ST-16). OPTIONAL and headers-only: this script
    # NEVER downloads it. When omitted, AMF encode is disabled and the bridge's
    # catra_encode_* return CATRA_ERR_NOT_IMPL gracefully. To enable AMF encode:
    #   1. Clone the GPUOpen AMF SDK (headers only; the runtime amfrt64.dll ships
    #      with the AMD Adrenalin driver — nothing to build or link):
    #        git clone https://github.com/GPUOpen-LibrariesAndSDKs/AMF lib/amf
    #      (lib/amf/ is gitignored.)
    #   2. Pass -AmfRoot lib/amf  (the repo root containing AMF/public/include/).
    # CMake then defines CATRA_HAS_AMF and lights up the AMF H.265 backend.
    [string]$AmfRoot = $env:CATRA_AMF_ROOT,

    # ONNX Runtime with DirectML (ST-13). OPTIONAL. The vcpkg onnxruntime-gpu
    # port pulls CUDA (wrong for this project). Instead, download the DirectML
    # build from NuGet:
    #   nuget install Microsoft.ML.OnnxRuntime.DirectML -OutputDirectory lib/onnxruntime
    # Then pass -OnnxRuntimeRoot lib/onnxruntime/<version>/build/native
    # (the directory containing include/ + lib/ + bin/ with onnxruntime.dll).
    # When omitted, RIFE compiles to a CATRA_ERR_NOT_IMPL stub (build still succeeds).
    [string]$OnnxRuntimeRoot = $env:CATRA_ONNXRUNTIME_ROOT,

    [switch]$SkipModelDownload
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$nativeDir  = Join-Path $repoRoot 'native/catra-gpu'
$buildDir   = Join-Path $nativeDir 'build'
$installDir = Join-Path $nativeDir 'install'

function Fail([string]$message) {
    Write-Error "build-native: $message"
    exit 1
}

# --- RIFE model (ST-13) -----------------------------------------------------
# The RIFE v4 ONNX model is a large binary (~10-20MB) and is NEVER committed
# (lib/rife/ is gitignored). This restores it on demand so the native bridge
# can load it at runtime. A failed download is a WARNING, not a fatal error:
# the DLL still builds; only live inference needs the file.
#
# Model sources (pick an arbitrary-timestep RIFE v4 export):
#   * https://github.com/hzwer/Practical-RIFE            (reference impl)
#   * community ONNX exports of Practical-RIFE / rife-ncnn-vulkan
# Override with -RifeModelUrl or $env:CATRA_RIFE_MODEL_URL.
function Ensure-RifeModel {
    if ($SkipModelDownload) {
        Write-Host "build-native: skipping RIFE model download (-SkipModelDownload)."
        return
    }
    $modelDir  = Join-Path $repoRoot 'lib/rife'
    $modelPath = Join-Path $modelDir 'rife_v4.onnx'
    if (Test-Path $modelPath) {
        Write-Host "build-native: RIFE model present -> $modelPath"
        return
    }
    $url = $env:CATRA_RIFE_MODEL_URL
    if ([string]::IsNullOrWhiteSpace($url)) { $url = $RifeModelUrl }
    Write-Host "build-native: downloading RIFE model from $url ..."
    try {
        New-Item -ItemType Directory -Force -Path $modelDir | Out-Null
        Invoke-WebRequest -Uri $url -OutFile $modelPath -UseBasicParsing
        Write-Host "build-native: RIFE model saved -> $modelPath"
    }
    catch {
        Write-Warning "build-native: RIFE model download failed ($($_.Exception.Message)). The DLL still builds; place rife_v4.onnx in lib/rife/ manually or set CATRA_RIFE_MODEL_PATH."
    }
}

# --- Locate vcpkg -----------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($VcpkgRoot)) {
    Fail "vcpkg not found. Set `$env:VCPKG_ROOT or pass -VcpkgRoot. See the header of this script for setup."
}
$vcpkgExe = Join-Path $VcpkgRoot 'vcpkg.exe'
if (-not (Test-Path $vcpkgExe)) {
    Fail "vcpkg.exe not found at '$vcpkgExe'. Run bootstrap-vcpkg.bat first."
}
$toolchainFile = Join-Path $VcpkgRoot 'scripts/buildsystems/vcpkg.cmake'
if (-not (Test-Path $toolchainFile)) {
    Fail "vcpkg toolchain file not found at '$toolchainFile'."
}

# --- Locate cmake -----------------------------------------------------------
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    Fail "cmake not found on PATH. Install CMake 3.25+."
}

Write-Host "build-native: repo root   = $repoRoot"
Write-Host "build-native: vcpkg root  = $VcpkgRoot"
Write-Host "build-native: config      = $Configuration"

# Restore the RIFE ONNX model before building so a fresh clone is runtime-ready.
Ensure-RifeModel

# --- Install dependencies (manifest mode) -----------------------------------
# vcpkg.json lives next to CMakeLists.txt; running `vcpkg install` there
# restores directx-headers + dxguid + onnxruntime-gpu (RIFE/DirectML) into
# native/catra-gpu/vcpkg_installed.
Write-Host "build-native: restoring vcpkg dependencies (x64-windows)..."
Push-Location $nativeDir
try {
    & $vcpkgExe install --triplet x64-windows
    if ($LASTEXITCODE -ne 0) { Fail "vcpkg install failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

# --- Configure --------------------------------------------------------------
Write-Host "build-native: configuring CMake..."

# FSR 4 (ST-14): forward the optional FidelityFX SDK location. When -FsrSdkRoot
# is empty CMake leaves CATRA_HAS_FSR4 undefined and the FSR 1 EASU fallback
# carries upscaling. The SDK is license-gated and is NEVER downloaded here.
$fsrArgs = @()
if (-not [string]::IsNullOrWhiteSpace($FsrSdkRoot)) {
    Write-Host "build-native: FSR 4 SDK root = $FsrSdkRoot"
    $fsrArgs += "-DCATRA_FSR_SDK_ROOT=$FsrSdkRoot"
    if (-not [string]::IsNullOrWhiteSpace($FsrSdkLib)) {
        $fsrArgs += "-DCATRA_FSR_SDK_LIB=$FsrSdkLib"
    }
}
else {
    Write-Host "build-native: FSR 4 SDK not supplied -> FSR 1 (EASU) fallback active."
}

# AMF H.265 encode (ST-16): forward the optional AMF SDK (headers-only) location.
# When -AmfRoot is empty CMake leaves CATRA_HAS_AMF undefined and catra_encode_*
# return CATRA_ERR_NOT_IMPL gracefully. The SDK is headers-only on GPUOpen and is
# NEVER downloaded here; the AMF runtime (amfrt64.dll) comes from the AMD driver.
$amfArgs = @()
if (-not [string]::IsNullOrWhiteSpace($AmfRoot)) {
    Write-Host "build-native: AMF SDK root = $AmfRoot"
    $amfArgs += "-DCATRA_AMF_ROOT=$AmfRoot"
}
else {
    Write-Host "build-native: AMF SDK not supplied -> H.265 encode disabled (CATRA_ERR_NOT_IMPL)."
}

# ONNX Runtime with DirectML (ST-13): forward the optional location.
# When omitted, RIFE compiles to a stub (build still succeeds).
$onnxArgs = @()
if (-not [string]::IsNullOrWhiteSpace($OnnxRuntimeRoot)) {
    # Resolve to absolute path (CMake resolves relative to CMAKE_CURRENT_SOURCE_DIR)
    $OnnxRuntimeRoot = (Resolve-Path $OnnxRuntimeRoot -ErrorAction Stop).Path
    Write-Host "build-native: ONNX Runtime root = $OnnxRuntimeRoot"
    $onnxArgs += "-DCATRA_ONNXRUNTIME_ROOT=$OnnxRuntimeRoot"
}
else {
    Write-Host "build-native: ONNX Runtime not supplied -> RIFE disabled (CATRA_ERR_NOT_IMPL)."
}

& cmake -B $buildDir -S $nativeDir `
    -DCMAKE_TOOLCHAIN_FILE="$toolchainFile" `
    -DVCPKG_TARGET_TRIPLET=x64-windows `
    -DCMAKE_BUILD_TYPE=$Configuration `
    @fsrArgs `
    @amfArgs `
    @onnxArgs
if ($LASTEXITCODE -ne 0) { Fail "CMake configure failed (exit $LASTEXITCODE)." }

# --- Build ------------------------------------------------------------------
Write-Host "build-native: building catra-gpu ($Configuration)..."
& cmake --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) { Fail "CMake build failed (exit $LASTEXITCODE)." }

# --- Install ----------------------------------------------------------------
# Places catra-gpu.dll under install/runtimes/win-x64/native/, the layout the
# .NET native probe path and CATRA.App.csproj expect.
Write-Host "build-native: installing to $installDir..."
& cmake --install $buildDir --prefix $installDir --config $Configuration
if ($LASTEXITCODE -ne 0) { Fail "CMake install failed (exit $LASTEXITCODE)." }

$dll = Join-Path $installDir 'runtimes/win-x64/native/catra-gpu.dll'
if (-not (Test-Path $dll)) {
    Fail "expected output DLL not found at '$dll'."
}

Write-Host "build-native: SUCCESS -> $dll"

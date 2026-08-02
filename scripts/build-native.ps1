# build-native.ps1 — build the catra-gpu native bridge (ST-12).
#
# Prerequisites (one-time, NOT installed by this script):
#   * Visual Studio 2022 with the "Desktop development with C++" workload
#   * Windows SDK 10.0.22621+ (D3D12 headers)
#   * CMake 3.25+ on PATH
#   * vcpkg: git clone https://github.com/microsoft/vcpkg; .\bootstrap-vcpkg.bat
#     then set $env:VCPKG_ROOT to the clone root (or pass -VcpkgRoot).
#
# Usage:
#   pwsh ./scripts/build-native.ps1 [-Configuration Release] [-VcpkgRoot <path>]
#
# Output:
#   native/catra-gpu/build/...                      (build tree)
#   native/catra-gpu/install/runtimes/win-x64/native/catra-gpu.dll
# The CATRA.App.csproj copies that installed DLL into the app output when it
# exists, so `dotnet build` picks it up automatically afterwards.

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$VcpkgRoot = $env:VCPKG_ROOT
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

# --- Install dependencies (manifest mode) -----------------------------------
# vcpkg.json lives next to CMakeLists.txt; running `vcpkg install` there
# restores directx-headers + dxguid into native/catra-gpu/vcpkg_installed.
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
& cmake -B $buildDir -S $nativeDir `
    -DCMAKE_TOOLCHAIN_FILE="$toolchainFile" `
    -DVCPKG_TARGET_TRIPLET=x64-windows `
    -DCMAKE_BUILD_TYPE=$Configuration
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

# install-libtorch-rocm.ps1 — Download and install libtorch with ROCm support
#
# This script downloads the standalone libtorch distribution (C++ API)
# which is required for ROCm backend in CATRA. The pip-installed PyTorch
# does NOT include C++ API headers.
#
# Prerequisites:
#   * Windows 11
#   * AMD Adrenalin driver 26.2.2 or newer
#   * Python 3.12 (for ROCm SDK)
#
# Usage:
#   pwsh ./scripts/install-libtorch-rocm.ps1 [-InstallDir "C:\libtorch"]
#
# After installation, rebuild the native bridge:
#   .\scripts\build-native-vs.bat

param(
    [string]$InstallDir = "$PSScriptRoot\..\lib\libtorch",
    [string]$PyTorchVersion = "2.9.1",
    [string]$ROCmVersion = "rocm7.2.1"
)

$ErrorActionPreference = 'Stop'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " libtorch ROCm Installation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Resolve install directory
$InstallDir = (Resolve-Path $InstallDir -ErrorAction SilentlyContinue) ?? $InstallDir
Write-Host "Install directory: $InstallDir" -ForegroundColor Yellow

# Create install directory
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Write-Host "Created directory: $InstallDir" -ForegroundColor Green
}

# Step 1: Install ROCm SDK via pip (required for GPU support)
Write-Host ""
Write-Host "Step 1: Installing ROCm SDK..." -ForegroundColor Cyan
python -m pip install --no-cache-dir `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install ROCm SDK"
    exit 1
}
Write-Host "✓ ROCm SDK installed" -ForegroundColor Green

# Step 2: Download libtorch
Write-Host ""
Write-Host "Step 2: Downloading libtorch..." -ForegroundColor Cyan
$libtorchUrl = "https://download.pytorch.org/libtorch/$ROCmVersion/libtorch-win-shared-with-deps-$PyTorchVersion%2B$ROCmVersion.zip"
$libtorchZip = Join-Path $InstallDir "libtorch.zip"

Write-Host "URL: $libtorchUrl" -ForegroundColor Gray
Write-Host "Downloading to: $libtorchZip" -ForegroundColor Gray

try {
    Invoke-WebRequest -Uri $libtorchUrl -OutFile $libtorchZip -UseBasicParsing
    Write-Host "✓ Download complete" -ForegroundColor Green
} catch {
    Write-Error "Failed to download libtorch: $_"
    exit 1
}

# Step 3: Extract libtorch
Write-Host ""
Write-Host "Step 3: Extracting libtorch..." -ForegroundColor Cyan
Expand-Archive -Path $libtorchZip -DestinationPath $InstallDir -Force
Remove-Item $libtorchZip
Write-Host "✓ Extraction complete" -ForegroundColor Green

# Step 4: Verify installation
Write-Host ""
Write-Host "Step 4: Verifying installation..." -ForegroundColor Cyan
$torchInclude = Join-Path $InstallDir "libtorch\include"
$torchLib = Join-Path $InstallDir "libtorch\lib"

if (Test-Path (Join-Path $torchInclude "torch\torch.h")) {
    Write-Host "✓ Headers found: $torchInclude" -ForegroundColor Green
} else {
    Write-Error "Headers not found at $torchInclude"
    exit 1
}

if (Test-Path (Join-Path $torchLib "torch_cpu.lib")) {
    Write-Host "✓ Libraries found: $torchLib" -ForegroundColor Green
} else {
    Write-Error "Libraries not found at $torchLib"
    exit 1
}

# Step 5: Set environment variable
Write-Host ""
Write-Host "Step 5: Setting environment variable..." -ForegroundColor Cyan
$libtorchPath = Join-Path $InstallDir "libtorch"
[Environment]::SetEnvironmentVariable("LIBTORCH_DIR", $libtorchPath, "User")
Write-Host "✓ LIBTORCH_DIR set to: $libtorchPath" -ForegroundColor Green

# Step 6: Add to PATH
$libtorchBin = Join-Path $libtorchPath "bin"
$currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($currentPath -notlike "*$libtorchBin*") {
    $newPath = "$currentPath;$libtorchBin"
    [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
    Write-Host "✓ Added to PATH: $libtorchBin" -ForegroundColor Green
} else {
    Write-Host "✓ Already in PATH: $libtorchBin" -ForegroundColor Green
}

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "libtorch installed at: $libtorchPath" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "1. Restart Visual Studio (to pick up environment variables)" -ForegroundColor White
Write-Host "2. Rebuild the native bridge:" -ForegroundColor White
Write-Host "   .\scripts\build-native-vs.bat" -ForegroundColor Cyan
Write-Host ""
Write-Host "3. Verify ROCm backend is enabled in build output:" -ForegroundColor White
Write-Host "   catra-gpu: PyTorch/libtorch found -> ROCm backend enabled" -ForegroundColor Green
Write-Host ""
Write-Host "Backend priority: ROCm -> DirectML -> CPU" -ForegroundColor Yellow

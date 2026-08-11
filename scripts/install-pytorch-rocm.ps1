# install-pytorch-rocm.ps1 — Install PyTorch with ROCm support for AMD GPUs
#
# Prerequisites:
#   * Windows 11 with AMD Adrenalin driver 26.2.2 or newer
#   * Python 3.12 installed
#   * AMD RX 9070 XT (RDNA 4) or compatible GPU
#
# Usage:
#   pwsh ./scripts/install-pytorch-rocm.ps1
#
# This installs:
#   * ROCm SDK 7.2.1 (core + devel + libraries)
#   * PyTorch 2.9.1 with ROCm support
#   * torchvision and torchaudio
#
# After installation, the build system will automatically detect PyTorch
# and use ROCm as the primary RIFE backend (priority: ROCm -> DirectML -> CPU).

$ErrorActionPreference = 'Stop'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " PyTorch ROCm Installation for AMD GPU" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check Python version
$pythonVersion = python --version 2>&1
if (-not ($pythonVersion -match "Python 3\.12")) {
    Write-Error "Python 3.12 is required. Found: $pythonVersion"
    Write-Host "Download from: https://www.python.org/downloads/" -ForegroundColor Yellow
    exit 1
}
Write-Host "✓ Python: $pythonVersion" -ForegroundColor Green

# Install ROCm SDK
Write-Host ""
Write-Host "Installing ROCm SDK 7.2.1..." -ForegroundColor Cyan
pip install --no-cache-dir `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install ROCm SDK"
    exit 1
}
Write-Host "✓ ROCm SDK installed" -ForegroundColor Green

# Install PyTorch with ROCm
Write-Host ""
Write-Host "Installing PyTorch 2.9.1 with ROCm support..." -ForegroundColor Cyan
Write-Host "(This may take several minutes)" -ForegroundColor Yellow
pip install --no-cache-dir `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install PyTorch"
    exit 1
}
Write-Host "✓ PyTorch installed" -ForegroundColor Green

# Verify installation
Write-Host ""
Write-Host "Verifying installation..." -ForegroundColor Cyan
$verifyResult = python -c "import torch; print(torch.cuda.is_available())" 2>&1
if ($verifyResult -eq "True") {
    Write-Host "✓ ROCm/HIP is available" -ForegroundColor Green
    
    $deviceName = python -c "import torch; print(torch.cuda.get_device_name(0))" 2>&1
    Write-Host "✓ GPU detected: $deviceName" -ForegroundColor Green
} else {
    Write-Warning "ROCm/HIP not available. Check AMD driver version (26.2.2+ required)"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Rebuild the native bridge:" -ForegroundColor White
Write-Host "   .\scripts\build-native-vs.bat" -ForegroundColor Cyan
Write-Host ""
Write-Host "2. The build will automatically detect PyTorch and enable ROCm backend" -ForegroundColor White
Write-Host ""
Write-Host "Backend priority: ROCm -> DirectML -> CPU" -ForegroundColor White

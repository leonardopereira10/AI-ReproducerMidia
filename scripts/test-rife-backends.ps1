# test-rife-backends.ps1 — Test and verify RIFE backend availability
#
# Usage:
#   pwsh ./scripts/test-rife-backends.ps1
#
# This script checks:
#   1. PyTorch/ROCm availability
#   2. DirectML availability
#   3. GPU device information
#   4. Backend priority chain

$ErrorActionPreference = 'Stop'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " RIFE Backend Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Test 1: PyTorch/ROCm
Write-Host "Test 1: PyTorch/ROCm" -ForegroundColor Yellow
try {
    $torchVersion = python -c "import torch; print(torch.__version__)" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ PyTorch version: $torchVersion" -ForegroundColor Green
        
        $cudaAvailable = python -c "import torch; print(torch.cuda.is_available())" 2>&1
        if ($cudaAvailable -eq "True") {
            Write-Host "  ✓ ROCm/HIP available" -ForegroundColor Green
            
            $deviceCount = python -c "import torch; print(torch.cuda.device_count())" 2>&1
            Write-Host "  ✓ GPU devices: $deviceCount" -ForegroundColor Green
            
            for ($i = 0; $i -lt $deviceCount; $i++) {
                $deviceName = python -c "import torch; print(torch.cuda.get_device_name($i))" 2>&1
                Write-Host "    - Device $i`: $deviceName" -ForegroundColor Cyan
            }
            
            Write-Host "  Status: READY" -ForegroundColor Green
        } else {
            Write-Host "  ✗ ROCm/HIP not available" -ForegroundColor Red
            Write-Host "  Status: UNAVAILABLE" -ForegroundColor Red
        }
    } else {
        Write-Host "  ✗ PyTorch not installed" -ForegroundColor Red
        Write-Host "  Status: NOT INSTALLED" -ForegroundColor Red
    }
} catch {
    Write-Host "  ✗ Error: $_" -ForegroundColor Red
    Write-Host "  Status: ERROR" -ForegroundColor Red
}

Write-Host ""

# Test 2: DirectML
Write-Host "Test 2: DirectML (ONNX Runtime)" -ForegroundColor Yellow
try {
    $onnxVersion = python -c "import onnxruntime; print(onnxruntime.__version__)" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ ONNX Runtime version: $onnxVersion" -ForegroundColor Green
        
        $dmlAvailable = python -c "import onnxruntime as ort; print('DirectMLExecutionProvider' in ort.get_available_providers())" 2>&1
        if ($dmlAvailable -eq "True") {
            Write-Host "  ✓ DirectML provider available" -ForegroundColor Green
            Write-Host "  Status: READY" -ForegroundColor Green
        } else {
            Write-Host "  ✗ DirectML provider not available" -ForegroundColor Red
            Write-Host "  Status: UNAVAILABLE" -ForegroundColor Red
        }
    } else {
        Write-Host "  ✗ ONNX Runtime not installed" -ForegroundColor Red
        Write-Host "  Status: NOT INSTALLED" -ForegroundColor Red
    }
} catch {
    Write-Host "  ✗ Error: $_" -ForegroundColor Red
    Write-Host "  Status: ERROR" -ForegroundColor Red
}

Write-Host ""

# Test 3: CPU Fallback
Write-Host "Test 3: CPU Fallback" -ForegroundColor Yellow
Write-Host "  ✓ Always available" -ForegroundColor Green
Write-Host "  Status: READY" -ForegroundColor Green

Write-Host ""

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Backend Priority Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$priority = @()
if ($cudaAvailable -eq "True") {
    $priority += "1. ROCm (PyTorch)"
}
if ($dmlAvailable -eq "True") {
    $priority += "$($priority.Count + 1). DirectML"
}
$priority += "$($priority.Count + 1). CPU"

foreach ($p in $priority) {
    Write-Host "  $p" -ForegroundColor White
}

Write-Host ""
Write-Host "Recommended action:" -ForegroundColor Yellow
if ($cudaAvailable -eq "True") {
    Write-Host "  ROCm is available and will be used automatically." -ForegroundColor Green
} elseif ($dmlAvailable -eq "True") {
    Write-Host "  DirectML is available. For better AMD GPU performance, install ROCm:" -ForegroundColor Yellow
    Write-Host "    .\scripts\install-pytorch-rocm.ps1" -ForegroundColor Cyan
} else {
    Write-Host "  No GPU backend available. Install ROCm or DirectML:" -ForegroundColor Red
    Write-Host "    .\scripts\install-pytorch-rocm.ps1" -ForegroundColor Cyan
}

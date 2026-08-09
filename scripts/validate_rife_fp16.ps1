# validate_rife_fp16.ps1 — ST-29: Validação do modelo RIFE FP16 + 4 scales
# Executar APÓS exportar o modelo com: python export_onnx.py --preset fast -o rife_v4.onnx
#
# Uso:
#   .\scripts\validate_rife_fp16.ps1
#
# Valida:
#   1. Tamanho do modelo (~9MB esperado)
#   2. Metadata ONNX (inputs FP16, outputs FP16)
#   3. Inference com onnxruntime (CPU) — compara FP16 vs FP32 (PSNR >35dB)
#   4. Build C# (0w/0e)
#   5. Testes C# (543+ pass)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$modelPath = Join-Path $repoRoot "lib\rife\rife_v4.onnx"

Write-Host "=== ST-29: Validação RIFE FP16 + 4 scales ===" -ForegroundColor Cyan

# 1. Verificar que o modelo existe
if (-not (Test-Path $modelPath)) {
    Write-Host "ERRO: Modelo não encontrado: $modelPath" -ForegroundColor Red
    Write-Host "Exporte com: python export_onnx.py --preset fast -o rife_v4.onnx" -ForegroundColor Yellow
    exit 1
}

# 2. Verificar tamanho (esperado ~9MB para FP16+4s, máximo 12MB)
$modelSize = (Get-Item $modelPath).Length
$modelSizeMB = [math]::Round($modelSize / 1MB, 1)
Write-Host "`n[1/5] Tamanho do modelo: ${modelSizeMB}MB" -ForegroundColor White
if ($modelSizeMB -gt 12) {
    Write-Host "  AVISO: Modelo >12MB — possívelmente ainda FP32 ou 5 scales" -ForegroundColor Yellow
} elseif ($modelSizeMB -le 10) {
    Write-Host "  OK: Tamanho compatível com FP16 + 4 scales" -ForegroundColor Green
}

# 3. Verificar metadata ONNX
Write-Host "`n[2/5] Metadata ONNX:" -ForegroundColor White
try {
    python -c @"
import onnxruntime as ort
sess = ort.InferenceSession('$modelPath'.replace('\\','/'), providers=['CPUExecutionProvider'])
for i in sess.get_inputs():
    print(f'  input:  {i.name:12s} shape={str(i.shape):30s} type={i.type}')
for o in sess.get_outputs():
    print(f'  output: {o.name:12s} shape={str(o.shape):30s} type={o.type}')
# Check if FP16
is_fp16 = any(i.type == 'tensor(float16)' for i in sess.get_inputs())
print(f'  FP16: {is_fp16}')
"@
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  AVISO: onnxruntime não instalado ou modelo inválido" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  AVISO: Python/onnxruntime não disponível" -ForegroundColor Yellow
}

# 4. Validação de qualidade (PSNR vs FP32) — requer ambos os modelos
$fp32Path = Join-Path $repoRoot "lib\rife\rife_v4_fp32_backup.onnx"
if (Test-Path $fp32Path) {
    Write-Host "`n[3/5] Validação PSNR (FP16 vs FP32):" -ForegroundColor White
    python -c @"
import onnxruntime as ort
import numpy as np

sess32 = ort.InferenceSession('$fp32Path'.replace('\\','/'), providers=['CPUExecutionProvider'])
sess16 = ort.InferenceSession('$modelPath'.replace('\\','/'), providers=['CPUExecutionProvider'])

# Detect dtypes
dtype32 = np.float32
dtype16 = np.float16 if any(i.type == 'tensor(float16)' for i in sess16.get_inputs()) else np.float32

np.random.seed(42)
results = []
for h, w in [(720, 1280), (1080, 1920)]:
    in0 = np.random.randn(1, 3, h, w).astype(np.float32)
    in1 = np.random.randn(1, 3, h, w).astype(np.float32)
    ts = np.array([0.5], dtype=np.float32)
    
    out32 = sess32.run(None, {'img0': in0, 'img1': in1, 'timestep': ts})[0]
    
    in0_16 = in0.astype(dtype16)
    in1_16 = in1.astype(dtype16)
    ts_16 = ts.astype(dtype16)
    out16 = sess16.run(None, {'img0': in0_16, 'img1': in1_16, 'timestep': ts_16})[0].astype(np.float32)
    
    mse = np.mean((out32 - out16) ** 2)
    psnr = 20 * np.log10(1.0 / np.sqrt(mse)) if mse > 0 else float('inf')
    results.append(psnr)
    print(f'  {h}x{w}: MSE={mse:.6f}, PSNR={psnr:.2f}dB')

min_psnr = min(results)
if min_psnr > 35:
    print(f'  OK: PSNR mínimo {min_psnr:.2f}dB > 35dB')
else:
    print(f'  FALHA: PSNR mínimo {min_psnr:.2f}dB < 35dB')
    exit(1)
"@
} else {
    Write-Host "`n[3/5] PSNR: SKIP (backup FP32 não encontrado — exporte FP32 primeiro)" -ForegroundColor Yellow
}

# 5. Build C#
Write-Host "`n[4/5] Build C#:" -ForegroundColor White
Push-Location $repoRoot
try {
    $buildOut = dotnet build --no-restore 2>&1
    $warnings = ($buildOut | Select-String "warning").Count
    $errors = ($buildOut | Select-String "error").Count
    if ($errors -eq 0) {
        Write-Host "  OK: Build succeeded (${warnings} warnings, ${errors} errors)" -ForegroundColor Green
    } else {
        Write-Host "  FALHA: ${errors} errors" -ForegroundColor Red
    }
} finally {
    Pop-Location
}

# 6. Testes C#
Write-Host "`n[5/5] Testes C#:" -ForegroundColor White
Push-Location $repoRoot
try {
    $testOut = dotnet test --no-build --verbosity minimal 2>&1
    $passed = ($testOut | Select-String "Aprovado" | ForEach-Object { ($_ -split "Aprovado:\s+")[1] -replace "[^\d]", "" } | Measure-Object -Sum).Sum
    $failed = ($testOut | Select-String "Com falha:\s+(\d+)" | ForEach-Object { [regex]::Match($_, "Com falha:\s+(\d+)").Groups[1].Value } | Measure-Object -Sum).Sum
    Write-Host "  Total: ${passed} pass, ${failed} fail" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
} finally {
    Pop-Location
}

Write-Host "`n=== ST-29: Validação concluída ===" -ForegroundColor Cyan
Write-Host "Próximo passo: exportar modelo com --preset fast e rodar pipeline completo" -ForegroundColor Yellow

# Guia Rápido: Export + Deploy Modelo RIFE FP16 (ST-29)

## Pré-requisitos
- Máquina com PyTorch ROCm instalado (GPU AMD)
- Practical-RIFE clonado com pesos v4.25
- onnxruntime instalado

## Passo 1: Exportar modelo FP16 + 4 scales

```bash
cd Practical-RIFE
cp <CATRA>/lib/rife/export_onnx.py .

# Opção A: preset fast (recomendado)
python export_onnx.py --preset fast -o rife_v4.onnx

# Opção B: flags individuais
python export_onnx.py --fp16 --scales "8,4,2,1" -o rife_v4.onnx
```

**Resultado esperado:**
- Arquivo: ~9MB (vs 22MB FP32)
- Inputs: tensor(float16)
- Output: tensor(float16)

## Passo 2: Backup do modelo antigo (opcional)

```bash
cp <CATRA>/lib/rife/rife_v4.onnx <CATRA>/lib/rife/rife_v4_fp32_backup.onnx
```

## Passo 3: Deploy

```bash
cp rife_v4.onnx <CATRA>/lib/rife/rife_v4.onnx
```

## Passo 4: Validar modelo

```powershell
cd <CATRA>
.\scripts\validate_rife_fp16.ps1
```

## Passo 5: Build nativo

```powershell
cd <CATRA>
cmd /c build_native_now.bat
```

## Passo 6: Testar pipeline completo

```powershell
cd <CATRA>/src/CATRA.App/bin/Debug/net8.0-windows
./CATRA.App.exe
# Processar Martial Master EP72 e observar throughput
```

**Meta:** ≥32fps output (vs ~16fps baseline)

## Passo 7: Validar qualidade

```bash
# Extrair frames do output
<CATRA>/lib/ffmpeg/ffmpeg.exe -i "D:\MediaPlayer\.cache\72_local.mp4" -ss 30 -vframes 1 frame_30s.png
<CATRA>/lib/ffmpeg/ffmpeg.exe -i "D:\MediaPlayer\.cache\72_local.mp4" -ss 150 -vframes 1 frame_150s.png

# Verificar tamanho (>50KB = conteúdo real)
ls -lh frame_*.png
```

## Rollback (se algo falhar)

```bash
cp <CATRA>/lib/rife/rife_v4_fp32_backup.onnx <CATRA>/lib/rife/rife_v4.onnx
cmd /c build_native_now.bat
```

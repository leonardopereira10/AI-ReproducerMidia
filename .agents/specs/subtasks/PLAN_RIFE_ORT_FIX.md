# Plano: Habilitar RIFE Interpolation (Opção A)

> **Prompt para novo chat:** Cole o conteúdo abaixo como primeira mensagem.

---

## PROMPT

```
🚀 Implementar RIFE Interpolation — Opção A

Leia .agents/specs/subtasks/PLAN_RIFE_ORT_FIX.md e execute os passos descritos.
CWD: C:/Projetos/Reprodutor_CATRA

## CONTEXTO

O pipeline de pre-processamento CATRA já executa end-to-end EXCETO a interpolação
de frames (RIFE v4). Dois problemas bloqueiam:

1. **Modelo RIFE ausente** — `lib/rife/rife_v4.onnx` não existe. O Practical-RIFE
   (github.com/hzwer/Practical-RIFE) é PyTorch-only; não há release ONNX oficial.
   Precisa exportar manualmente.

2. **ONNX Runtime API version mismatch** — O NuGet `Microsoft.ML.OnnxRuntime.DirectML`
   v1.18.1 reporta "Current ORT Version is: 1.17.1" e suporta API versions [1, 17].
   O código nativo em `interp_rife.cpp` pede API version 18 (via `OrtGetApiBase()->GetApi(18)`).

## ESTADO ATUAL

- Pipeline funciona: FFmpeg decode → (interp skip) → FSR1 upscale → AMF encode → mux ✅
- catra-gpu.dll compila e carrega ✅
- FFmpeg.AutoGen 8.1.0 + LibraryVersionMap auto-patch ✅
- Graceful skip implementado (commit 73b3cfa) ✅
- Erro atual no log: `catra_interp_create failed with error code -1`
  + `The requested API version [18] is not available, only [1, 17] are supported`

## PASSOS DE IMPLEMENTAÇÃO

### PASSO 1 — Fix ORT API version (nativo)

**Arquivo:** `native/catra-gpu/interp_rife.cpp`

O problema: `OrtGetApiBase()->GetApi(ORT_API_VERSION)` onde `ORT_API_VERSION` = 18
(macro do header onnxruntime_c_api.h v1.18), mas a DLL runtime é v1.17.1.

**Fix:** Usar version negotiation — pedir a maior versão disponível até 17:

```cpp
// Em vez de:
// const OrtApi* g_ort = OrtGetApiBase()->GetApi(ORT_API_VERSION);

// Usar:
static const OrtApi* GetOrtApi() {
    const OrtApiBase* base = OrtGetApiBase();
    // Try from highest known down to 1 (the DLL reports max supported).
    for (uint32_t v = ORT_API_VERSION; v >= 1; --v) {
        const OrtApi* api = base->GetApi(v);
        if (api != nullptr) return api;
    }
    return nullptr; // should never happen if ORT is loaded
}
static const OrtApi* g_ort = GetOrtApi();
```

Alternativa mais simples (se o header define ORT_API_VERSION como 18 mas a DLL
suporta 17): hardcode `GetApi(17)` ou use `GetAvailableApiVersion()`:

```cpp
uint32_t maxVer = 0;
OrtGetApiBase()->GetAvailableApiVersion(&maxVer);
const OrtApi* g_ort = OrtGetApiBase()->GetApi(maxVer);
```

**Verificar:** `GetAvailableApiVersion` existe desde ORT 1.12+. Confirmar no header.

Após fix, rebuildar:
```powershell
Remove-Item -Recurse -Force native\catra-gpu\build
.\scripts\build-native.ps1 -OnnxRuntimeRoot "lib\onnxruntime\microsoft.ml.onnxruntime.directml\1.18.1" -AmfRoot "lib\amf" -SkipModelDownload
```

### PASSO 2 — Exportar modelo RIFE v4 ONNX

**Pré-requisitos:** Python 3.10+, PyTorch 2.x, onnx, onnxruntime

```bash
# 1. Clone Practical-RIFE
git clone https://github.com/hzwer/Practical-RIFE
cd Practical-RIFE

# 2. Baixar pesos treinados (v4.x — ver README do repo para links)
#    Normalmente: train_log/RIFE_HDv3/ (ou similar)
#    Links comuns: Google Drive / Baidu no README

# 3. Instalar deps
pip install torch torchvision onnx onnxruntime numpy

# 4. Script de export (salvar como export_onnx.py):
```

```python
"""Export Practical-RIFE v4 to ONNX for CATRA native bridge."""
import torch
import torch.nn as nn
from model.RIFE_HDv3 import Model  # adjust import to match repo structure

# Load model
model = Model()
model.load_model("train_log", -1)  # -1 = latest checkpoint
model.eval()
model.cuda()  # or .cpu() if no GPU for export

# The native bridge (interp_rife.cpp) expects:
#   Inputs:  img0 [1,3,H,W] float32, img1 [1,3,H,W] float32, timestep [1] float32
#   Output:  flow/middle frame [1,3,H,W] float32
#
# Practical-RIFE's forward takes (img0, img1, timestep) and returns the
# interpolated frame. Wrap it to match the expected signature.

class RifeWrapper(nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, img0, img1, timestep):
        # Practical-RIFE expects timestep as scalar or [B,1,1,1]
        # Normalize to [0,1] range if needed
        return self.model.inference(img0, img1, timestep=timestep.item())

wrapper = RifeWrapper(model).cuda()
wrapper.eval()

# Dummy inputs (720p reference; dynamic axes handle other resolutions)
H, W = 720, 1280
img0 = torch.randn(1, 3, H, W, device='cuda')
img1 = torch.randn(1, 3, H, W, device='cuda')
timestep = torch.tensor([0.5], device='cuda')

with torch.no_grad():
    torch.onnx.export(
        wrapper,
        (img0, img1, timestep),
        "rife_v4.onnx",
        input_names=["img0", "img1", "timestep"],
        output_names=["output"],
        dynamic_axes={
            "img0": {0: "batch", 2: "height", 3: "width"},
            "img1": {0: "batch", 2: "height", 3: "width"},
            "output": {0: "batch", 2: "height", 3: "width"},
        },
        opset_version=17,  # match ORT 1.17.x
        do_constant_folding=True,
    )

print("✅ Exported rife_v4.onnx")
```

```bash
# 5. Run export
python export_onnx.py

# 6. Copy to CATRA
cp rife_v4.onnx <CATRA_REPO>/lib/rife/rife_v4.onnx
```

**NOTA:** O `interp_rife.cpp` nativo precisa que os nomes dos tensores de
input/output batam com o que o código espera. Verificar no código nativo
quais nomes são usados (`"img0"`, `"img1"`, `"timestep"` / `"output"` ou
`"flow"`) e ajustar o export ou o código nativo conforme necessário.

### PASSO 3 — Ajustar interp_rife.cpp para nomes de tensor

**Arquivo:** `native/catra-gpu/interp_rife.cpp`

Verificar as strings de input/output names usadas em `OrtSession::Run()`.
O modelo exportado acima usa `["img0", "img1", "timestep"]` → `["output"]`.
Se o código nativo usa nomes diferentes, ajustar um dos dois lados.

### PASSO 4 — Validar end-to-end

1. Rebuild nativo com modelo presente:
```powershell
.\scripts\build-native.ps1 -OnnxRuntimeRoot "lib\onnxruntime\microsoft.ml.onnxruntime.directml\1.18.1" -AmfRoot "lib\amf"
```
(omitir `-SkipModelDownload` para tentar baixar, ou colocar manualmente)

2. Limpar jobs falhos:
```python
import sqlite3, os
db = os.path.join(os.environ['APPDATA'], 'CATRA', 'catra.db')
conn = sqlite3.connect(db)
conn.execute('DELETE FROM ProcessJob')
conn.commit()
conn.close()
```

3. Rodar app, clicar Iniciar, verificar:
   - Log mostra `interp_rife: model loaded`
   - Progresso avança past 35% (interp weight)
   - Arquivo `.mp4` aparece em `D:\MediaPlayer\.cache\` (ou pasta configurada)

### PASSO 5 — Alternativa: ORT 1.18+ com DirectML

Se o NuGet 1.18.1 tem DLL v1.17.1 internamente (bug de packaging), tentar:
- NuGet `Microsoft.ML.OnnxRuntime.DirectML` v1.19+ (se disponível)
- Ou build manual do ONNX Runtime com `--use_dml` flag

```bash
git clone --recursive https://github.com/microsoft/onnxruntime
cd onnxruntime
git checkout v1.18.1
./build.bat --config Release --use_dml --build_shared_lib --parallel
```

Isso produz `onnxruntime.dll` com API v18 + DirectML EP.

## ARQUIVOS A MODIFICAR

| Arquivo | Mudança |
|---------|---------|
| `native/catra-gpu/interp_rife.cpp` | ORT API version negotiation + tensor names |
| `lib/rife/rife_v4.onnx` | Modelo exportado (gitignored) |
| `lib/rife/README.md` | Atualizar com script de export funcional |

## CRITÉRIOS DE SUCESSO

- [ ] `catra_interp_create` retorna 0 (sucesso)
- [ ] Log mostra `interp_rife: model loaded (rife_v4.onnx), EP=DirectML`
- [ ] Pipeline completa com progresso 100%
- [ ] Arquivo processado aparece na pasta de output
- [ ] `dotnet build` + `dotnet test` continuam verdes
- [ ] Build nativo continua compilando sem o modelo (stub graceful)

## DEPENDÊNCIAS EXTERNAS

- Python 3.10+ com PyTorch (para export do modelo)
- GPU com CUDA ou CPU para rodar o export (não precisa ser AMD)
- ~2GB RAM para o export do modelo
- ONNX Runtime 1.17+ com DirectML (já no NuGet)

## ESTIMATIVA

- Fix ORT version: 15 min
- Export modelo: 30-60 min (download pesos + export + debug tensor names)
- Validação: 15 min
- **Total: ~1-2h**
```

---

## NOTAS ADICIONAIS

- O `interp_rife.cpp` tem dois caminhos: `#if defined(USE_DML)` (GPU) e fallback CPU.
  O CMake detecta DirectML EP via compile-time probe. Com o NuGet DirectML, o
  header `dml_provider_factory.h` está presente mas o probe `CATRA_DML_EP_AVAILABLE`
  falha porque o método `AppendExecutionProvider_DML` não está no C++ wrapper
  (está no C API via `OrtSessionOptionsAppendExecutionProvider_DML`).
  
  **Fix alternativo para DML:** usar a C API diretamente:
  ```cpp
  #include "dml_provider_factory.h"
  OrtSessionOptionsAppendExecutionProvider_DML(sessionOptions, 0); // device 0
  ```
  Em vez de `sessionOptions.AppendExecutionProvider_DML(0)` (C++ wrapper).

- Se o modelo RIFE for muito pesado para CPU-only (sem DirectML), considerar
  reduzir resolução de processamento ou usar RIFE-lite.

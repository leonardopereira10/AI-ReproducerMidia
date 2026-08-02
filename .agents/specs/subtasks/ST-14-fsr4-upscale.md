# Subtask 14: FSR 4 SDK Integration (Upscale)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (FSR 4, RF-03, RN-07)
Depende de ST-12 (native bridge skeleton).
**Risco: CRITICAL** — SDK recente, RDNA 4 required.

## Objetivo
Implementar upscale via FSR 4 SDK (FidelityFX, DX12 Compute) na native bridge.
Input: textura GPU (720p/1080p) → output: textura upscaled (1080p/4K).
Fallback: FSR 1 (espacial) se FSR 4 indisponível.

## Escopo
### Arquivos a Criar
- `native/catra-gpu/upscale_fsr4.cpp` — implementação FSR 4
- `native/catra-gpu/upscale_fsr4.h`
- `native/catra-gpu/upscale_fsr1.cpp` — fallback FSR 1 (compute shader simples)
- `native/catra-gpu/upscale_fsr1.h`
- `lib/fsr-sdk/` — FidelityFX FSR 4 SDK (GPUOpen)
- `tests/native/test_upscale.cpp` — teste nativo standalone

### Arquivos a Modificar
- `native/catra-gpu/catra_gpu.cpp` — implementar `catra_upscale_*`
- `native/catra-gpu/CMakeLists.txt` — adicionar FSR SDK includes/libs
- `scripts/build-native.ps1` — download FSR SDK se ausente

### Arquivos a Modificar (C#)
- `src/CATRA.Services/Processing/NativeBridge.cs` — expor upscale methods

### Arquivos NÃO tocar
- `native/catra-gpu/interp_rife.cpp` — ST-13
- `native/catra-gpu/encode_amf.cpp` — ST-16

## Requisitos Técnicos

### FSR 4 SDK (FidelityFX)
- Source: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK
- FSR 4 requer: RDNA 4 (RX 9070 XT ✅), DX12, Windows 10+
- API: DX12 Compute shaders via FidelityFX SDK dispatch
- Quality modes: Quality, Balanced, Performance, Ultra Performance
  - Para 720p→4K: Ultra Performance (4x)
  - Para 1080p→4K: Quality (2x)
  - Para 720p→1080p: Quality (1.5x)
- Input: `ID3D12Resource` (texture, DXGI_FORMAT_R8G8B8A8_UNORM ou R16G16B16A16_FLOAT)
- Output: `ID3D12Resource` upscaled

### catra_upscale_create
```cpp
int catra_upscale_create(int src_w, int src_h, int dst_w, int dst_h,
                         int method, int* out_ctx)
// method: 0=off, 1=fsr1, 2=fsr4
```
- Se method=2: verificar `catra_is_fsr4_available()`
  - Se não disponível: log warning + downgrade para method=1
- Se method=0: ctx é passthrough (copia textura)
- FSR 4: criar `ffx::Context`, configurar `ffx::UpscaleContext::CreateDesc`
  - `maxRenderSize = {dst_w, dst_h}`
  - `displaySize = {dst_w, dst_h}`
  - `qualityMode` baseado no ratio
- FSR 1: criar compute shader pipeline (EASU shader do FSR 1)
- Alocar texturas intermediárias se necessário

### catra_upscale_process
```cpp
int catra_upscale_process(int ctx, void* src_texture, void** dst_texture)
```
- Input: `ID3D11Texture2D*` (do decoder ou interp)
- D3D11→DX12 interop (ST-15): shared texture → `ID3D12Resource*`
- FSR 4: `ffx::UpscaleContext::Dispatch(desc)` — roda compute shader
- FSR 1: dispatch EASU compute shader
- Output: `ID3D12Resource*` upscaled → share back para D3D11
- Retornar ponteiro da textura output

### catra_is_fsr4_available
```cpp
int catra_is_fsr4_available()
```
- Verificar adapter D3D12: `DXGI_ADAPTER_DESC` → VendorID == AMD (0x1002)
- Verificar RDNA 4: device ID ou feature level
- Tentar criar FSR 4 context → se falhar, retornar 0
- Retornar 1 se OK, 0 se não

### FSR 1 Fallback
- FSR 1 EASU (Edge Adaptive Spatial Upsampling): compute shader simples
- Source: FidelityFX-FSR1 (GPUOpen)
- Não requer hardware específico — qualquer GPU DX12
- Qualidade inferior ao FSR 4 mas funcional
- Shader HLSL compilado via `dxc` ou pré-compilado DXIL

### RN-07: Skip Upscale
```
SE src_h >= dst_h → catra_upscale_create com method=0 (passthrough)
```
- Caller (pipeline orchestration) decide se precisa upscale
- Native bridge só executa o que pedem

## Critérios de Sucesso
- [ ] `cmake --build` compila com FSR SDK
- [ ] `catra_is_fsr4_available()` retorna 1 na RX 9070 XT
- [ ] FSR 4 upscale 720p→4K funcional (textura output correta)
- [ ] FSR 4 upscale 1080p→4K funcional
- [ ] FSR 4 upscale 720p→1080p funcional
- [ ] FSR 1 fallback funcional (simular FSR 4 indisponível)
- [ ] Passthrough (method=0) copia textura sem alteração
- [ ] P/Invoke do C# funciona (NativeBridge.UpscaleCreate/Process/Destroy)
- [ ] Sem leak de GPU memory após 100 frames
- [ ] Qualidade visual: FSR 4 > FSR 1 (comparação side-by-side)

## Dependências
- ST-12 (native bridge skeleton + CMake)
- ST-15 (D3D11↔DX12 interop) — pode ser desenvolvida em paralelo, mas necessária para teste end-to-end

## Notas
- **RISCO CRÍTICO:** FSR 4 SDK é recente (lançou com RDNA 4, 2025)
  - API pode mudar entre versões do SDK
  - Pin version específica do FidelityFX SDK
  - Se SDK não compilar ou não funcionar: FSR 1 segura o MVP da Fase 2
- FSR 4 é ML-based (usa aceleradores ML da RDNA 4) — diferente do FSR 1/2 (shaders)
- FidelityFX SDK usa C++ com wrappers — verificar se há C API ou se precisa wrapper
- Alternativa: FSR 2/3 (temporal, DX12) como meio-termo entre FSR 1 e FSR 4
- Para vídeo offline: FSR 4 quality mode pode ser mais agressivo que em jogos
  (sem preocupação com latency)
- Testar com frame real de anime (720p) → comparar com bicubic/Lanczos

# Subtask 13: RIFE v4 Integration (Frame Interpolation)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Frame Interpolation, RF-03)
Depende de ST-12 (native bridge skeleton).
**Risco: CRITICAL** — primeira integração ML no pipeline.

## Objetivo
Implementar frame interpolation via RIFE v4 (ONNX Runtime + DirectML) na
native bridge. Input: par de frames GPU → output: N frames intermediários.
Suporte a arbitrary timestep (24fps → 135fps, 24fps → 55fps).

## Escopo
### Arquivos a Criar
- `native/catra-gpu/interp_rife.cpp` — implementação RIFE
- `native/catra-gpu/interp_rife.h`
- `lib/rife/rife_v4.onnx` — modelo ONNX (download)
- `lib/onnxruntime/` — ONNX Runtime + DirectML NuGet/binaries
- `tests/native/test_interp.cpp` — teste nativo standalone

### Arquivos a Modificar
- `native/catra-gpu/catra_gpu.cpp` — implementar `catra_interp_*` com RIFE
- `native/catra-gpu/CMakeLists.txt` — adicionar ONNX Runtime, DirectML
- `native/catra-gpu/vcpkg.json` — adicionar onnxruntime-gpu
- `src/CATRA.Services/Processing/NativeBridge.cs` — expor interp methods
- `scripts/build-native.ps1` — download modelo RIFE se ausente

### Arquivos NÃO tocar
- `native/catra-gpu/upscale_fsr4.cpp` — ST-14
- `native/catra-gpu/encode_amf.cpp` — ST-16

## Requisitos Técnicos

### RIFE v4 via ONNX Runtime
- Modelo: RIFE v4.x (practical-rife ou rife-ncnn-vulkan, exportar para ONNX)
- ONNX Runtime com **DirectML** execution provider (GPU AMD)
- Input: 2 frames RGB float32 [1, 3, H, W] normalizados [0,1]
- Output: 1 frame intermediário RGB float32 [1, 3, H, W]
- Arbitrary timestep: RIFE suporta `timestep` parameter (0.0–1.0)
  - Para 24→135fps: ratio = 24/135, gerar frames em t=1/5.625, 2/5.625, ...
  - Para 24→55fps: ratio = 24/55, gerar frames em t=1/2.29, ...

### catra_interp_create
```cpp
int catra_interp_create(int src_w, int src_h, double src_fps,
                        double target_fps, int method, int* out_ctx)
```
- Calcular `interp_ratio = target_fps / src_fps`
- Calcular `frames_per_pair = ceil(interp_ratio) - 1` (frames intermediários por par)
- Carregar modelo ONNX, criar InferenceSession com DirectML EP
- Alocar buffers GPU (input/output tensors)
- Retornar contexto opaco

### catra_interp_process
```cpp
int catra_interp_process(int ctx, void* frame_a, void* frame_b,
                         void** out_frames, int* out_count)
```
- Input: 2 texturas D3D11 (frame_a, frame_b)
- Para cada timestep t em [1/N, 2/N, ..., (N-1)/N]:
  - Preparar input: frame_a + frame_b + timestep
  - Run ONNX inference
  - Output: frame intermediário como textura D3D11
- `out_frames`: array de texturas (caller libera)
- `out_count`: quantidade de frames gerados
- Performance target: ~50ms por par de frames 1080p (offline, não crítico)

### catra_interp_destroy
- Liberar InferenceSession, buffers, contexto

### DirectML Setup
- `Ort::SessionOptions::AppendExecutionProvider_DML(d3d12_device)`
- Se DirectML falhar → fallback CPU (lento mas funcional)
- Log: "RIFE interp: DirectML EP ativo" ou "RIFE interp: CPU fallback"

### Modelo RIFE
- Download: practical-rife ou rife-ncnn-vulkan ONNX export
- Tamanho: ~10-20MB
- Salvar em `lib/rife/rife_v4.onnx`
- Script de download no build (se não existir)

## Critérios de Sucesso
- [ ] `cmake --build` compila com ONNX Runtime + DirectML
- [ ] `catra_interp_create` carrega modelo ONNX sem erro
- [ ] DirectML EP ativo (confirmado via log)
- [ ] 24fps → 135fps: gera ~4.6 frames intermediários por par
- [ ] 24fps → 55fps: gera ~1.3 frames intermediários por par
- [ ] Frames intermediários visualmente coerentes (sem artifacts óbvios)
- [ ] Teste nativo: processa 10 pares de frames sem crash
- [ ] P/Invoke do C# funciona (NativeBridge.InterpCreate/Process/Destroy)
- [ ] CPU fallback funciona se DirectML indisponível
- [ ] Memory: sem leak após 100 calls (monitorar com Task Manager)

## Dependências
- ST-12 (native bridge skeleton + CMake)

## Notas
- **RISCO CRÍTICO:** ONNX Runtime + DirectML em RDNA 4 é relativamente novo
  - Validar cedo: primeiro testar inference simples (1 par de frames)
  - Se DirectML falhar em RDNA 4: tentar CUDA EP (não tem GPU NVIDIA) ou CPU
- RIFE ONNX models: https://github.com/hzwer/Practical-RIFE ou exports da comunidade
- Alternativa: rife-ncnn-vulkan (Vulkan compute) — mas spec diz DX12 only
- Arbitrary timestep: nem todos os modelos RIFE suportam — verificar
  - Se não suportar: usar modelo 2x/4x/8x e resample para target fps
- Performance não é crítica (offline) — qualidade > velocidade
- Frames são texturas D3D11 compartilhadas — não copiar para CPU

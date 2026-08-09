# 🏎️ Sprint Plan: Pipeline GPU-Only — Eliminar Round-Trips CPU↔GPU

**Objetivo:** Acelerar o pipeline de pré-processamento CATRA de ~16 fps para ≥32 fps (2x)
eliminando transferências CPU↔GPU desnecessárias.

**Criado:** 2026-08-09
**Status:** PLANEJAMENTO
**Baseline:** 542 testes, build 0w/0e, commit df89fce

---

## 📊 Análise de Throughput Atual

### Pipeline por par de frames (24fps→135fps, 5 interpolações/par)

| Etapa | Operação | Tempo est. |
|-------|----------|-----------|
| **Decoder** | av_hwframe_transfer_data (GPU→CPU 3MB NV12) | ~8ms |
| **Decoder** | sws_scale NV12→BGRA (CPU, 2M px) | ~10ms |
| **Decoder** | Map+Copy+CopyResource (CPU→GPU 8MB BGRA) | ~7ms |
| **RIFE ×5** | TextureToTensor (CopyResource+Map+CPU loop BGRA→float) | ~7ms ×2 = 14ms |
| **RIFE ×5** | DirectML inference | ~45ms ×5 = 225ms |
| **RIFE ×5** | TensorToTexture (CPU loop float→BGRA+Map+Copy) | ~5ms ×5 = 25ms |
| **Interop ×7** | ReadBack11ToCpu (staging+Flush+WaitIdle+Map) | ~3ms ×7 = 21ms |
| **Interop ×7** | UploadCpuTo12 (Map+CopyTextureRegion+fence) | ~2ms ×7 = 14ms |
| **Encode ×7** | AMF SubmitInput+QueryOutput | ~3ms ×7 = 21ms |
| **Overhead** | Sync, alloc, orchestrator | ~112ms |
| **TOTAL** | | **~462ms/par** |

**Output fps:** 1000ms / (462ms / 6 frames) = **~13 fps de saída** (pior que medido;
os 16fps reais sugerem ~420ms/par — overhead é menor que estimado, mas o perfil relativo vale).

### Decomposição: onde o tempo vai

- **RIFE inference:** 225ms (54%) — bottleneck principal, fora do escopo deste sprint
- **CPU↔GPU transfers:** ~110ms (26%) — **alvo principal deste sprint**
- **Interop CPU round-trip:** ~35ms (8%) — **alvo**
- **Encode:** ~21ms (5%)
- **Overhead:** ~112ms (27%) — parcialmente eliminável com as otimizações

---

## 🔍 Análise de Viabilidade das Abordagens (A-E)

### A) GPU shader NV12→BGRA no decoder ✅ RECOMENDADA — ALTO IMPACTO

**O que:** Criar compute shader HLSL que lê NV12 via SRV (Y plane R8 + UV plane R8G8)
e escreve BGRA em UAV (R8G8B8A8). Executa inteiramente na GPU.

**Elimina:** av_hwframe_transfer_data + sws_scale + BGRA upload (~25-30ms/frame)

**Viabilidade:** ALTA
- SRV de NV12 é técnica padrão (todo player de vídeo faz)
- D3D11 permite SRV em subresource específico (ArraySlice + FirstArraySlice)
- Planos NV12 (DXGI_FORMAT_R8_UNORM para Y, DXGI_FORMAT_R8G8_UNORM para UV) são suportados
- Independente dos bugs do driver AMD com CopySubresourceRegion (caminho totalmente diferente)
- Vortice.Direct3D11 3.5.0 expõe CreateShaderResourceView, CreateUnorderedAccessView, CreateComputeShader, Dispatch

**Risco:** BAIXO
- Se SRV em array slice NV12 falhar neste driver → fallback para CPU path existente
- Shader é ~30 linhas de HLSL (BT.601 full-range)
- Compilação runtime via d3dcompiler_47.dll (Windows SDK, sempre presente)

**Implementação:**
- C++ nativo: `catra_nv12_to_bgra(ID3D11Texture2D* arrayTex, UINT arraySlice, w, h, &outBgra)`
- Novo arquivo: `native/catra-gpu/nv12_to_bgra_shader.cpp`
- C#: FrameDecoder.OwnFrame() chama via P/Invoke em vez do path CPU atual
- Fallback: se catra_nv12_to_bgra retornar CATRA_ERR_DEVICE, FrameDecoder usa o path CPU

### B) D3D11 VideoProcessor (ID3D11VideoDevice) ⚠️ VIÁVEL MAS ARRISCADO

**O que:** API hardware do Windows para conversão NV12→BGRA.

**Viabilidade:** MÉDIA
- Menos código que shader (3-4 calls vs ~30 linhas HLSL)
- Usa o Video Processor hardware (mais rápido que shader genérico)

**Risco:** MÉDIO-ALTO
- Pode ter os mesmos bugs do driver AMD com array slices NV12 (não testado)
- ID3D11VideoContext::VideoProcessorSetInputStream com array slice: não documentado
- Fallback mais complexo se falhar
- Dependência de feature level e driver support

**Veredito:** NÃO usar como primária. Pode ser alternativa se (A) falhar.

### C) Workaround do driver AMD (investigação) ❌ NÃO RECOMENDADO

**O que:** Testar empiricamente variações de CopySubresourceRegion.

**Viabilidade:** BAIXA
- Já testamos: CopySubresourceRegion, CopyResource, Map UV — todos falham
- Flush() antes, srcBox explícito, ArraySize menor — sem evidência de que funcione
- Bug de driver pode ser intratável (firmware/microcode)

**Risco:** ALTO — pode consumir dias sem resultado

**Veredito:** DESCARTAR como sprint principal. Se (A) funcionar, não precisamos mais disso.

### D) Restaurar encode GPU (commit aee1166) ✅ RECOMENDADO — MÉDIO IMPACTO

**O que:** Com decoder emitindo BGRA (não NV12), o caminho GPU-GPU do interop
(keyed mutex + shared NT handle) pode funcionar.

**Elimina:** ReadBack11ToCpu + UploadCpuTo12 por frame (~5ms/frame = ~35ms/par)

**Viabilidade:** MÉDIA-ALTA
- BGRA share D3D11→D3D12 funciona (verificado com interop_readback_test)
- Código já existe (commit aee1166), precisa ser reativado
- AMF já está Init com AMF_SURFACE_BGRA

**Risco:** MÉDIO
- Keyed mutex pode ter problemas pós-DirectML (verificado antes)
- Pode precisar do WaitForD3D11GpuIdle() para sync cross-API
- Precisa validar com catra-interop-test.exe antes de integrar

**Implementação:**
- d3d_interop.cpp: quando formato é BGRA, usar pooled GPU-GPU copy (não CPU round-trip)
- Manter CPU round-trip como fallback se GPU copy falhar

### E) Decode paralelo (producer/consumer) ⚠️ MARGINAL

**O que:** Pipeline duplo: thread A decode+download, thread B converte+upload.

**Viabilidade:** BAIXA-MÉDIA
- D3D11 immediate context não é thread-safe (precisa deferred context ou lock)
- Ganho marginal (2-4x no decode, mas ainda CPU-bound)
- Complexidade alta para ganho pequeno

**Risco:** MÉDIO — threading + D3D11 = bugs difíceis

**Veredito:** FORA DO ESCOPO. As otimizações GPU (A+D) eliminam o decode CPU inteiramente.

---

## 🎯 Estratégia Recomendada

### Ordem de execução (por impacto/risco):

1. **ST-23: GPU shader NV12→BGRA no decoder** (abordagem A) — ALTO impacto, BAIXO risco
2. **ST-24: Restaurar interop GPU-GPU para BGRA** (abordagem D) — MÉDIO impacto, MÉDIO risco
3. **ST-25: RIFE GPU tensor I/O** (bônus) — MÉDIO impacto, MÉDIO risco
4. **ST-26: Validação GPU real + profiling**

### Estimativa de throughput final

| Otimização | Economia/par (ms) |
|------------|-------------------|
| ST-23: Decoder GPU shader | ~25-30ms |
| ST-24: Interop GPU copy | ~30-35ms |
| ST-25: RIFE GPU tensors | ~25-30ms |
| Redução overhead (menos sync/alloc) | ~40-50ms |
| **Total economia** | **~120-145ms** |
| **Novo tempo/par** | **~275-300ms** |
| **Output fps estimado** | **~20-22 fps (6 frames/par ÷ 275-300ms)** |

> ⚠️ **REALIDADE:** 32fps exige ~187ms/par. O bottleneck é RIFE inference (225ms/par = 5×45ms).
> Para atingir 32fps é necessário **também** otimizar o RIFE (FP16, batching, ou modelo menor).
> Este sprint elimina os round-trips CPU e cria a base para otimizações futuras.
> **Meta realista: 20-22 fps** (1.3-1.4x do atual), com caminho claro para 32fps via RIFE FP16.

---

## 📋 Subtasks

### ST-23: GPU Compute Shader NV12→BGRA no Decoder

**Complexidade:** alta
**Arquivos:**
- `native/catra-gpu/nv12_to_bgra_shader.cpp` (NOVO)
- `native/catra-gpu/nv12_to_bgra_shader.h` (NOVO)
- `native/catra-gpu/catra_gpu.h` (adicionar C ABI)
- `native/catra-gpu/catra_gpu.cpp` (registrar C ABI)
- `src/CATRA.Services/Processing/FrameDecoder.cs` (modificar OwnFrame)
- `src/CATRA.Core/Processing/NativeBridge.cs` (P/Invoke)

**Implementação:**

1. **HLSL Compute Shader** (embedded como string C++):
```hlsl
// NV12 (R8 Y + R8G8 UV) → BGRA, BT.601 full-range
RWTexture2D<float4> output : register(u0);
Texture2D<float> yTex : register(t0);
Texture2D<float2> uvTex : register(t1);

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    float Y = yTex.Load(uint3(dtid.xy, 0)).r;
    float2 UV = uvTex.Load(uint3(dtid.xy / 2, 0)).rg;
    float U = UV.x - 0.5;
    float V = UV.y - 0.5;
    float R = Y + 1.402 * V;
    float G = Y - 0.344136 * U - 0.714136 * V;
    float B = Y + 1.772 * U;
    output[dtid.xy] = float4(B, G, R, 1.0);
}
```

2. **C++ nativo** (`nv12_to_bgra_shader.cpp`):
- `catra_nv12_to_bgra_init(device)` — compila shader, cria pipeline state
- `catra_nv12_to_bgra(device, context, nv12ArrayTex, arraySlice, w, h, &outBgra)` —
  cria SRV Y (R8, ArraySlice), SRV UV (R8G8, ArraySlice), UAV BGRA, dispatch,
  retorna standalone BGRA texture
- `catra_nv12_to_bgra_shutdown()` — libera recursos do pipeline
- Compilação: D3DCompile() via d3dcompiler_47.dll (P/Invoke no C++)

3. **C# FrameDecoder**:
- Em OwnFrame(): tentar `catra_nv12_to_bgra()` primeiro
- Se retornar CATRA_OK: usar textura BGRA retornada (skip av_hwframe_transfer_data + sws_scale + upload)
- Se retornar erro: fallback para o path CPU atual
- Adicionar log diagnóstico comparando output GPU vs CPU no primeiro frame

**Critérios de aceite:**
- [ ] `dotnet build` 0w/0e
- [ ] `dotnet test` 542 passed, 0 failed
- [ ] Build nativo via build_native_now.bat sem erros
- [ ] GPU shader produz BGRA visualmente idêntico ao CPU path (validar com dump comparativo)
- [ ] Fallback CPU funciona quando GPU shader falha
- [ ] Não introduz regressão nos testes existentes

**Dependências:** Nenhuma

---

### ST-24: Restaurar Interop GPU-GPU Copy para BGRA

**Complexidade:** média
**Arquivos:**
- `native/catra-gpu/d3d_interop.cpp` (modificar interop_share_d3d11_to_d3d12)
- `native/catra-gpu/tools/interop_readback_test.cpp` (validação)

**Implementação:**

1. **d3d_interop.cpp — interop_share_d3d11_to_d3d12**:
- Quando formato é BGRA (não NV12): usar pooled GPU-GPU copy (pooled path do commit aee1166)
  - CopyResource D3D11→shared slot (GPU-GPU, zero CPU traffic)
  - Flush() + WaitForD3D11GpuIdle() (sync cross-API)
  - Keyed mutex ReleaseSync
- Quando formato é NV12: manter CPU round-trip (fallback de segurança)
- Adicionar log: "interop: using GPU-GPU copy (BGRA)" vs "interop: using CPU round-trip"

2. **Validação com interop_readback_test**:
- Compilar e executar tools/interop_readback_test.cpp
- Validar que BGRA pooled copy produz dados corretos no D3D12
- Testar com frames reais do decoder (após ST-23)

3. **AMF encode path**:
- Com interop GPU-GPU funcionando, AMF recebe texturas D3D12 via shared handle + keyed mutex
- CreateSurfaceFromDX12Native zero-copy (sem CPU traffic)
- Validar que AMF aceita BGRA via shared surface (já está Init com AMF_SURFACE_BGRA)

**Critérios de aceite:**
- [ ] `dotnet build` 0w/0e
- [ ] `dotnet test` 542 passed, 0 failed
- [ ] Build nativo sem erros
- [ ] interop_readback_test.exe: BGRA pooled copy = PASS
- [ ] Encode de 10+ frames sem erros AMF
- [ ] CPU round-trip permanece como fallback (não removido)

**Dependências:** ST-23 (decoder BGRA é pré-requisito para pipeline 100% BGRA)

---

### ST-25: RIFE GPU Tensor I/O (BGRA↔float32 via Compute Shader)

**Complexidade:** alta
**Arquivos:**
- `native/catra-gpu/interp_rife.cpp` (modificar TextureToTensor/TensorToTexture)
- `native/catra-gpu/rife_tensor_shader.cpp` (NOVO)
- `native/catra-gpu/rife_tensor_shader.h` (NOVO)

**Implementação:**

1. **BGRA→float32 compute shader** (substitui TextureToTensor BGRA path):
```hlsl
// BGRA texture → planar RGB float32 UAV buffer
Texture2D<float4> input : register(t0);
RWBuffer<float> output : register(u0); // [3*H*W]: R plane, G plane, B plane

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    uint pixelIdx = dtid.y * WIDTH + dtid.x;
    float4 bgra = input.Load(uint3(dtid.xy, 0));
    output[pixelIdx] = bgra.b;                    // R plane
    output[pixelIdx + WIDTH * HEIGHT] = bgra.g;   // G plane
    output[pixelIdx + 2 * WIDTH * HEIGHT] = bgra.r; // B plane
}
```

2. **float32→BGRA compute shader** (substitui TensorToTexture):
```hlsl
// Planar RGB float32 UAV buffer → BGRA texture
RWBuffer<float> input : register(u0);
RWTexture2D<float4> output : register(u1);

[numthreads(16, 16, 1)]
void main(uint3 dtid : SV_DispatchThreadID) {
    uint pixelIdx = dtid.y * WIDTH + dtid.x;
    float r = input[pixelIdx];
    float g = input[pixelIdx + WIDTH * HEIGHT];
    float b = input[pixelIdx + 2 * WIDTH * HEIGHT];
    output[dtid.xy] = float4(b, g, r, 1.0);
}
```

3. **ORT GPU tensor integration**:
- Opção A (simples): shader escreve em D3D11 buffer → Map → memcpy para tensor ORT CPU
  - Elimina o loop CPU pixel-por-pixel (que é o gargalo, não o Map)
  - Ganho: ~5-8ms/frame (loop CPU é lento; GPU shader é instant)
- Opção B (avançada): alocar tensor ORT em GPU memory via DML
  - Requer OrtMemoryInfo com DML-specific allocator
  - Mais complexo, ganho maior (elimina Map+memcpy também)
  - Implementar apenas se Opção A não der ganho suficiente

4. **RifeContext changes**:
- Adicionar: pipeline state para os 2 shaders, UAV buffer para tensor
- Lazy init: criar shaders no primeiro Process (quando formato conhecido)
- Fallback: manter TextureToTensor/TensorToTexture CPU como fallback

**Critérios de aceite:**
- [ ] `dotnet build` 0w/0e
- [ ] `dotnet test` 542 passed, 0 failed
- [ ] Build nativo sem erros
- [ ] Output visual idêntico ao CPU path (comparar PNGs)
- [ ] Fallback CPU funciona quando GPU shader falha
- [ ] Ganho mensurável: ≥5ms/frame vs CPU path

**Dependências:** ST-23 (pipeline BGRA)

---

### ST-26: Validação GPU Real + Profiling

**Complexidade:** média
**Arquivos:** Nenhum (validação apenas)

**Implementação:**

1. **Build nativo completo** via build_native_now.bat
2. **Teste de integração** com Martial Master EpisodeId=72 (~6 min):
   - Processar episódio completo
   - Medir throughput (fps de saída)
   - Validar output: ffprobe (HEVC 1920x1080 @135fps + AAC)
   - Validar qualidade: PNGs em 30s/150s/300s (comparar com baseline)
3. **Profiling detalhado**:
   - Adicionar timestamps no pipeline C# (Stopwatch por etapa)
   - Medir: decoder/frame, RIFE/pair, interop/frame, encode/frame
   - Comparar com baseline (462ms/par)
4. **Catra-interop-test**:
   - Compilar e executar com BGRA pooled copy
   - Validar readback correto

**Critérios de aceite:**
- [ ] Episódio completo processado sem erros
- [ ] Throughput ≥20 fps de saída (meta mínima)
- [ ] Output .mp4 validado com ffprobe
- [ ] Qualidade visual equivalente ao baseline (PNGs comparáveis)
- [ ] 542 testes continuam passando
- [ ] Relatório de profiling documentado no SPRINT_LOG

**Dependências:** ST-23, ST-24, ST-25

---

## 📊 Matriz de Risco

| Subtask | Prob. falha | Impacto falha | Rollback |
|---------|-------------|---------------|----------|
| ST-23 | Baixa (10%) | Alto (decoder não funciona) | Fallback CPU automático |
| ST-24 | Média (25%) | Médio (encode lento) | Fallback CPU round-trip |
| ST-25 | Média (20%) | Médio (RIFE lento) | Fallback CPU tensors |
| ST-26 | Baixa (5%) | Baixo (apenas medição) | N/A |

---

## 🚫 Fora do Escopo

- **ST-20/21/22:** Em andamento/pendentes, não tocar
- **RIFE FP16/batching:** Otimização de modelo, sprint separado
- **Producer/consumer parallelism:** Marginal com GPU-only path
- **Workaround driver AMD CopySubresourceRegion:** Não necessário com shader
- **D3D11 VideoProcessor:** Alternativa para ST-23 se shader falhar

---

## 📦 Commits Atômicos Esperados

1. `feat(ST-23): GPU compute shader NV12→BGRA no decoder`
2. `feat(ST-24): restaurar interop GPU-GPU copy para BGRA`
3. `feat(ST-25): RIFE GPU tensor I/O via compute shader`
4. `chore(ST-26): validação GPU real + profiling report`

---

## 🔗 Dependências Graph

```
ST-23 (decoder GPU shader)
  ├──→ ST-24 (interop GPU copy)
  │      └──→ ST-26 (validação)
  ├──→ ST-25 (RIFE GPU tensors)
  │      └──→ ST-26 (validação)
  └──────────→ ST-26 (validação)
```

ST-24 e ST-25 podem ser executados em paralelo após ST-23.
ST-26 requer todos os anteriores.

---

## 📝 Notas de Implementação

### D3DCompiler no C++
- `d3dcompiler_47.dll` é parte do Windows SDK (sempre presente)
- P/Invoke: `D3DCompile(src, srcLen, name, defines, include, entry, target, flags1, flags2, out blob, out errors)`
- Entry: "main", Target: "cs_5_0" (compute shader)
- Compilar no init, cache o blob

### SRV de NV12 array slice
```cpp
D3D11_SHADER_RESOURCE_VIEW_DESC srvY = {};
srvY.Format = DXGI_FORMAT_R8_UNORM;
srvY.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
srvY.Texture2DArray.FirstArraySlice = arraySlice;
srvY.Texture2DArray.ArraySize = 1;
srvY.Texture2DArray.MipLevels = 1;

D3D11_SHADER_RESOURCE_VIEW_DESC srvUV = {};
srvUV.Format = DXGI_FORMAT_R8G8_UNORM;
srvUV.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
srvUV.Texture2DArray.FirstArraySlice = arraySlice;
srvUV.Texture2DArray.ArraySize = 1;
srvUV.Texture2DArray.MipLevels = 1;
```

### Fallback strategy
Cada subtask deve ter fallback transparente:
- ST-23: se shader falha → FrameDecoder usa path CPU atual (log warning)
- ST-24: se GPU copy falha → interop usa CPU round-trip (log warning)
- ST-25: se shader falha → RIFE usa TextureToTensor/TensorToTexture CPU

### Variável de ambiente para debug
- `CATRA_GPU_SHADER_DEBUG=1`: dump output do shader vs CPU path no primeiro frame
- `CATRA_INTEROP_DEBUG`: já existe, manter

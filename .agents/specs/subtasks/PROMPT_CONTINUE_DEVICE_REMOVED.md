# Prompt: Continuar Fix DXGI_ERROR_DEVICE_REMOVED

> **Cole este conteúdo como primeira mensagem em uma nova sessão do Pi.**

---

## PROMPT

```
🐛 fix bug — DXGI_ERROR_DEVICE_REMOVED no encode AMF após RIFE interpolação

Leia PRIMEIRO, nesta ordem:
1. `.agents/specs/subtasks/PLAN_INTEROP_DEVICE_REMOVED.md` (plano de correção)
2. `.agents/sprint_atual/SPRINT_LOG.md` (estado da sprint + histórico)
3. `.agents/specs/subtasks/PROMPT_CONTINUE_DEVICE_REMOVED.md` (este arquivo — contexto completo)
CWD: C:/Projetos/Reprodutor_CATRA

## PROBLEMA

O pipeline de pré-processamento CATRA executa:
  FFmpeg decode (NV12/D3D11VA) → RIFE interp (DirectML/D3D11) → FSR upscale → AMF encode (D3D12) → mux

A interpolação RIFE funciona perfeitamente (commit eff437e validado em GPU real),
mas o encode AMF falha com DXGI_ERROR_DEVICE_REMOVED (0x887A0001) após processar
o primeiro par de frames.

## O QUE JÁ FOI FEITO (commits no main)

### Commit eff437e — RIFE ORT fix (VALIDADO ✅)
- ORT API version negotiation (header v18 / DLL v1.17.1)
- DML probe: compile-based → file-existence
- Graceful degradation test
- Build: 0w/0e, 542 testes

### Commit 498c645 — Interop fix (NÃO VALIDADO ⚠️)
3 bugs corrigidos no código fonte:
1. NT handle leak em catra_encode_frame (RAII ShareCleanup → CloseHandle)
2. D3D12 resource refcount leak (ShareCleanup → Release)
3. g_frameKey não resetado no pool rebuild (d3d_interop.cpp)

**O build nativo foi refeito COM MSVC** (Build Tools 2022):
```
build_native_now.bat → build-native.ps1 -VcpkgRoot "C:\Projetos\vcpkg" -OnnxRuntimeRoot "lib\onnxruntime\microsoft.ml.onnxruntime.directml\1.18.1" -AmfRoot "lib\amf"
```
DLL nova: `native/catra-gpu/install/runtimes/win-x64/native/catra-gpu.dll` (md5: e6d5b978)
Deployada em: `src/CATRA.App/bin/Debug/net8.0-windows/runtimes/win-x64/native/catra-gpu.dll` (md5 igual ✅)

## O QUE FALTA FAZER

### 1. Validar que o fix funciona (CRÍTICO)
O app CATRA está rodando (pid pode variar). Precisa:
1. Abrir o app: `dotnet run --project src/CATRA.App 2>/tmp/catra_stderr.log`
2. Clicar na série "A Record of a Mortal's Journey"
3. Clicar no botão "📥 Iniciar" para iniciar pre-processamento
4. Aguardar ~10 segundos
5. Verificar o stderr:
   - ✅ SUCESSO: Múltiplas linhas "ProcessInterpolation done: count=5" SEM "DEVICE_REMOVED"
   - ❌ FALHA: Ainda aparece "ReleaseSync(key=1) hr=0x887A0001" ou "SubmitInput failed"

### 2. Se ainda falha — diagnóstico aprofundado
O ReleaseSync(key=1) falha pode indicar que o **device morreu DURANTE a operação de copy**,
não por causa do keyed mutex. Possíveis causas adicionais:

a) **D3D11 immediate context ordering**: O CopyResource do interop (D3D11 immediate context)
   pode estar executando enquanto DirectML ainda tem operações GPU pendentes no mesmo device.
   → Testar: adicionar `g_d3d11Context->Flush()` antes do AcquireSync no interop.

b) **TDR (Timeout Detection and Recovery)**: A carga combinada RIFE(DirectML) + FSR1(D3D12)
   + interop copies + AMF encode pode exceder o TDR timeout (2s default).
   → Testar: adicionar registry `TdrDelay = 10` (requires admin + reboot)
   → Ou: adicionar `g_d3d12Queue->Wait(g_fence, fenceValue)` após o copy para garantir
     que o D3D11 copy completou antes do AMF acessar o recurso D3D12.

c) **D3D12 fence sync ausente**: O interop faz CopyResource no D3D11 timeline mas
   o AMF acessa o mesmo recurso via D3D12. Sem fence cross-queue, o AMF pode ler
   dados incompletos.
   → Fix: Após CopyResource + ReleaseSync, signal fence no D3D11 context e Wait no D3D12.
   → Arquivos: d3d_interop.cpp (adicionar Signal + Wait no pooled copy path)

d) **AMF e interop usam D3D12 queues diferentes**: O interop cria sua própria DIRECT queue,
   mas o AMF cria outra. Recursos compartilhados entre queues diferentes precisam de sync.

### 3. Arquivos suspeitos para investigação adicional
| Arquivo | O que verificar |
|---------|----------------|
| `native/catra-gpu/d3d_interop.cpp` | Faltou D3D12 fence sync após CopyResource? |
| `native/catra-gpu/encode_amf.cpp` | AMF usa qual D3D12 queue? Há sync com o interop? |
| `native/catra-gpu/upscale_fsr1.cpp` | FSR1 faz dispatch D3D12 — mesmo queue do interop? |
| `native/catra-gpu/catra_gpu.cpp` | catra_encode_frame: o interop+encode flow está correto? |
| `native/catra-gpu/d3d_interop.h` | Contrato de ownership do D3D12 resource está claro? |

## AMBIENTE

- MSVC Build Tools 2022 disponível em: `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\`
- Build script: `build_native_now.bat` (na raiz do repo)
- vcpkg: `C:\Projetos\vcpkg`
- ONNX Runtime DirectML: `lib/onnxruntime/microsoft.ml.onnxruntime.directml/1.18.1/`
- AMF SDK: `lib/amf/` (headers-only, runtime via AMD driver)
- SEM VS2022 IDE — apenas Build Tools + CMake + Ninja
- GPU: AMD (RDNA 4, DirectML support)
- Python 3.12 disponível para scripts

## RESTRIÇÕES
- NÃO tocar em ST-20/21/22 (em andamento/pendentes)
- Código nativo validado por INSPEÇÃO + build script (MSVC Build Tools)
- Camada C# testada com fakes (542 testes)
- O app tem um "AMD Bug Report Tool" que aparece após crash de GPU — ignore-o

## CRITÉRIOS DE ACEITE
- [ ] Pipeline completa múltiplos frame pairs SEM DXGI_ERROR_DEVICE_REMOVED
- [ ] Log mostra múltiplas "ProcessInterpolation done" + "EncodeFrame" sem falha
- [ ] `dotnet build` + `dotnet test` continuam verdes (542+ testes, 0w/0e)
- [ ] Arquivo .mp4 processado aparece na pasta de output
```

---

## CONTEXTO TÉCNICO ADICIONAL

### Log do crash (pré-fix, DLL antiga — mesmo após rebuild a falha pode persistir):
```
[catra-gpu] interop_init: D3D12 device + DIRECT queue ready (adapter shared with D3D11)
[catra-gpu] catra_init: D3D11<->DX12 interop ready (shared adapter)
[catra-gpu] interp_rife: model loaded (rife_v4.onnx), EP=DirectML, 1920x1080, 5 frames/pair (ratio=5.400)
[catra-gpu] interp_rife: timestep input 'timestep' resolved at index 2
[catra-gpu] encode_amf: HEVC encoder ready 1920x1080 @ 135.000 fps, 20000 kbps (tier=Main)
[RunFrameLoop] ProcessInterpolation: frame=1
interp_rife: Process ENTER ctx=0 frameA=... frameB=...
interp_rife: frames validated OK
interp_rife: staging textures created (format=103, 1920x1080)
interp_rife: TextureToTensor A...
interp_rife: TextureToTensor B...
interp_rife: tensors ready, starting inference loop N=5
[RunFrameLoop] ProcessInterpolation done: count=5                    ← RIFE OK!
[catra-gpu] interop: pool rebuilt 1920x1080 fmt=103 (4 slots)        ← NV12 pool
[catra-gpu] interop: pool rebuilt 1920x1080 fmt=87 (4 slots)         ← BGRA pool (rebuild!)
[catra-gpu] interop: pool ReleaseSync(key=1) hr=0x887A0001           ← FALHA no 2º pool
[catra-gpu] encode_amf: SubmitInput failed (res=1)                   ← CONSEQUÊNCIA
```

### Análise do engineer subagent (resumida):
- O `pool rebuilt` aparece DUAS VEZES porque o formato muda (NV12→BGRA).
- No segundo rebuild, `g_frameKey` NÃO era resetado (fix commit 498c645).
- Mas o ReleaseSync falha pode ser sintoma, não causa — o device pode ter morrido
  por TDR ou falta de cross-queue fence sync entre D3D11 (interop copy) e D3D12 (AMF).
- O AMF encoder usa `CreateSurfaceFromDX12Native` (zero-copy wrap), então ele
  depende de o D3D12 resource estar pronto. Se o CopyResource D3D11 ainda não
  completou quando o AMF tenta ler, DEVICE_REMOVED.

### Fluxo de ownership (pooled copy path):
```
RIFE TensorToTexture() → D3D11 texture (MiscFlags=0, caller-owned)
         ↓
interop_share_d3d11_to_d3d12() → CopyResource para pool slot (SHARED_KEYEDMUTEX)
         ↓                          AcquireSync(key) → Copy → ReleaseSync(key)
         ↓                          Retorna: slot.res12 (AddRef'd) + new NT handle
         ↓
AMF Encode(d3d12res) → CreateSurfaceFromDX12Native → submit to HW encoder
         ↓
ShareCleanup ~ShareCleanup() → CloseHandle(handle) + d3d12res->Release()
```

### Possível fix adicional (D3D12 fence sync):
```cpp
// Em d3d_interop.cpp, após CopyResource + ReleaseSync no POOLED COPY path:
// Signal a fence on the D3D11 side and wait on the D3D12 side.
// Isso garante que o copy completou antes do AMF acessar o resource.

// Opção A: D3D11 Query + ID3D11DeviceContext::GetData
// Opção B: Shared fence (D3D11 + D3D12 same fence object)
// Opção C: CPU stall via Flush() + Signal/Wait (simples mas bloqueia)
```

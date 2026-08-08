# Fix: DXGI_ERROR_DEVICE_REMOVED (0x887A0001) no Encode AMF

## Causa Raiz (diagnóstico via engineer subagent)

3 bugs concorrentes no interop D3D11→D3D12 + encode:

### Bug 1: NT Handle Leak (CRÍTICO)
**Arquivo:** `native/catra-gpu/catra_gpu.cpp` — `catra_encode_frame` (~L752)
**Problema:** `interop_share_d3d11_to_d3d12` retorna `sharedHandle` (via `CreateSharedHandle`), 
mas `catra_encode_frame` nunca chama `CloseHandle(sharedHandle)`.
**Impacto:** ~1 handle leaked por frame. Para um filme de 2h a 135fps = ~972k handles.

### Bug 2: D3D12 Resource Refcount Leak (ALTO)
**Arquivo:** `native/catra-gpu/catra_gpu.cpp` — `catra_encode_frame`
**Problema:** `interop_share_d3d11_to_d3d12` faz `slot.res12->AddRef()` para o caller.
O comentário diz "do NOT release" mas o pool NUNCA libera o refcount extra.
**Impacto:** Refcount acumula; em pool rebuild os slots antigos não são liberados corretamente.

### Bug 3: Keyed Mutex Key Desync (ALTO)
**Arquivo:** `native/catra-gpu/d3d_interop.cpp` — `EnsurePoolLocked` (~L230)
**Problema:** Pool rebuild reseta `g_poolIndex = 0` mas NÃO reseta `g_frameKey`.
Slots novos têm keyed mutexes zeradas; se `g_frameKey = 1` após rebuild, 
o consumer AMF (`consumerKey = 0`) bloqueia no `AcquireSync(0)` → timeout.
**Impacto:** Deadlock após mudança de formato (NV12→BGRA).

## Fix Scope

| Arquivo | Mudança | Linhas |
|---------|---------|--------|
| `catra_gpu.cpp` | CloseHandle + Release d3d12res após encode | ~5 linhas |
| `d3d_interop.cpp` | Reset `g_frameKey = 0` no rebuild | ~1 linha |

Total: 2 arquivos, ~6 linhas de mudança nativa + testes C#.

## Plano de Execução

### Subtask 1: Fix nativo (developer-critical)
1. `catra_gpu.cpp` catra_encode_frame: `CloseHandle(sharedHandle)` após `Encode`
2. `catra_gpu.cpp` catra_encode_frame: `d3d12res->Release()` após `Encode`  
3. `d3d_interop.cpp` EnsurePoolLocked: `g_frameKey = 0` após `g_poolIndex = 0`

### Subtask 2: Testes C# (qa-tester-critical)
- Verificar que o pool rebuild reseta a key (teste inspeção)
- Verificar build + test verdes (477+ testes, 0 falhas)

### Subtask 3: Validação real (manual via computer use)
- Rodar pre-processamento de 1 episódio
- Verificar que pipeline completa past frame 1 sem DEVICE_REMOVED
- Verificar que múltiplos frames são processados

## Restrições
- SEM MSVC/vcpkg → código nativo validado por INSPEÇÃO
- Build C# testado com `dotnet build` + `dotnet test`
- NÃO tocar em ST-20/21/22

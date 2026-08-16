# Diagnóstico: AccessViolationException + ReleaseSync Failing

## Resumo Executivo

O upscale async implementado está causando **crashes (AccessViolationException)** e **fallbacks para CPU** devido a race conditions no código nativo. A implementação atual viola múltiplas garantias de thread-safety dos backends FSR1/FSR4 e do interop pool global.

## Problemas Identificados

### 1. Race Condition Crítica no Upscale Async (CAUSA DO CRASH)

**Localização**: `catra_gpu.cpp:228-295` (AsyncUpscaleWorker)

**Problema**: O worker thread chama `fsr1->Process()` e `fsr4->Process()` sem mutex, mas os headers declaram explicitamente:

```cpp
// upscale_fsr1.h:37
// THREADING: a single Fsr1Upscaler is NOT safe to process from multiple threads
// concurrently (it reuses one command allocator / descriptor heap).

// upscale_fsr4.h:35
// THREADING: a single Fsr4Upscaler is not safe for concurrent Process calls.
```

**Impacto**:
- Com processamento paralelo (`MaxParallelJobs > 1`), múltiplos workers acessam o mesmo `Fsr1Upscaler` simultaneamente
- Corrupção de memória interna dos backends (command allocators compartilhados)
- **AccessViolationException** quando threads acessam memória corrompida

**Correção Aplicada**: Adicionado `std::mutex processMutex` ao `UpscaleContext` e protegido todas as chamadas `Process()` com `std::lock_guard`.

### 2. Interop Pool Global Não É Thread-Safe (CAUSA DO ReleaseSync FALHANDO)

**Localização**: `d3d_interop.cpp:1073-1130` (PooledCopyD3D11Locked)

**Problema**: O pool de interop usa variáveis globais compartilhadas:

```cpp
std::vector<InteropPoolSlot> g_pool;           // Global - compartilhado entre todos os contextos
size_t g_poolIndex = 0;                        // Global - round-robin cursor
uint64_t g_frameKey = 0;                       // Global - keyed mutex key
```

Quando múltiplos vídeos processam em paralelo:
- Cada worker thread chama `ShareTexture` → `PooledCopyD3D11Locked`
- Todos competem pelo mesmo `g_mutex` (protege o pool)
- O `g_frameKey` alterna 0/1, mas com múltiplos producers, a sequência fica desordenada
- O encoder faz `AcquireSync(key)` esperando uma chave, mas recebe outra
- **ReleaseSync falha** com `DXGI_ERROR_INVALID_CALL (0x887A0001)`
- Fallback para CPU round-trip (lento)

**Impacto**:
- Perda de performance (CPU round-trip ao invés de GPU-GPU copy)
- Logs mostram: `interop: GPU-GPU copy ReleaseSync(key=0) hr=0x887A0001 — falling back to CPU round-trip`

### 3. Dangling Pointer no Worker Thread (RISCO DE CRASH)

**Localização**: `catra_gpu.cpp:330-360` (DestroyUpscale)

**Problema**: Quando `DestroyUpscale` é chamado:

```cpp
ctx = std::move(it->second);  // move para fora do map
g_upscaleContexts.erase(it);

// worker pode estar processando ctx->fsr1->Process() AGORA
ctx->asyncWorker.join();  // espera worker terminar
```

Se o worker está no meio de `ctx->fsr1->Process()` quando `ctx` é movido, o ponteiro interno do backend fica inválido.

**Correção Aplicada**: O `join()` agora acontece antes do contexto ser destruído (unique_ptr mantém contexto vivo até join completar).

## Soluções Propostas

### Opção A: Reverter para Upscale Síncrono (RECOMENDADO - CURTO PRAZO)

**Vantagens**:
- Elimina todos os problemas de thread-safety imediatamente
- Estável e previsível
- Não requer mudanças no interop pool

**Desvantagens**:
- Perde o ganho de performance do upscale async (GPU fica ociosa durante CPU work)
- Pipeline 3-stage ainda funciona, mas upscale é bloqueante

**Implementação**:
1. Reverter `ProcessingPipeline.cs` para usar `ProcessUpscale()` síncrono
2. Manter mutex no native bridge (já adicionado) para safety
3. Desabilitar `MaxParallelJobs` ou limitar a 1

### Opção B: Upscale Async com Pool por Contexto (RECOMENDADO - LONGO PRAZO)

**Vantagens**:
- Mantém performance do upscale async
- Elimina contenção no interop pool
- Permite processamento paralelo seguro

**Desvantagens**:
- Requer refatoração significativa do interop
- Maior uso de memória (múltiplos pools)
- Complexidade adicional

**Implementação**:
1. Criar `InteropPool` por `UpscaleContext` ao invés de global
2. Cada pool tem seu próprio `g_pool`, `g_poolIndex`, `g_frameKey`
3. Pool é criado quando contexto é criado, destruído quando contexto é destruído
4. Manter mutex no backend (já adicionado)

### Opção C: Fence-Based Sync ao Invés de Keyed Mutex (AVANÇADO)

**Vantagens**:
- Elimina completamente keyed mutexes (problemáticos no AMD RDNA 4)
- Sincronização mais robusta via D3D12 fences
- Melhor performance (sem CPU fallback)

**Desvantagens**:
- Refatoração complexa do interop
- Requer mudança no encoder AMF
- Risco de introduzir novos bugs

**Implementação**:
1. Substituir `IDXGIKeyedMutex` por `ID3D12Fence`
2. Producer sinaliza fence após copy
3. Consumer espera fence antes de ler
4. Eliminar `AcquireSync`/`ReleaseSync` completamente

## Recomendação

**Curto prazo (agora)**: Opção A - Reverter para upscale síncrono
- Estabilidade imediata
- Permite testar processamento paralelo sem crashes
- Performance ainda melhor que original (pipeline 3-stage + buffers)

**Longo prazo (próximo sprint)**: Opção B - Pool por contexto
- Mantém performance do upscale async
- Escalável para múltiplos vídeos
- Requer teste extensivo

**Futuro**: Opção C - Fence-based sync
- Melhor performance possível
- Elimina problemas do AMD driver
- Requer design cuidadoso

## Próximos Passos

1. **Imediato**: Reverter para upscale síncrono (Opção A)
2. **Testar**: Processamento com `MaxParallelJobs=2` para validar estabilidade
3. **Planejar**: Implementar Opção B no próximo sprint
4. **Monitorar**: GPU utilization deve subir para 40-60% com paralelismo

## Métricas de Sucesso

- **Estabilidade**: Zero crashes em 10+ vídeos processados
- **Performance**: GPU utilization > 40% com MaxParallelJobs=2
- **Qualidade**: Zero fallbacks para CPU round-trip
- **Throughput**: 2x mais vídeos/hora comparado ao original

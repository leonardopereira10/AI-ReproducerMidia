# Subtask 01 — EnqueueAsync: skip de jobs Completed com ProcessedFile válido + forceReprocess

**Story:** `.agents/sprint_atual/story_01_enqueue_skip_completed.md`
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Implementar a decisão de skip em `ProcessingQueueService.EnqueueAsync`: job
`Completed` cujo par (episode, profile) tenha `ProcessedFile` válido — arquivo
existe em disco (`File.Exists`) E `ProcessedFile.SourceHash` confere com
`Episode.FileHash` atual — **não** é re-enfileirado. Nova flag opcional
`forceReprocess = false` na interface permite bypassar o skip.

**Regras de negócio (da story):**

| Situação | Comportamento |
|---|---|
| `Completed` + `ProcessedFile` válido | skip (job fica `Completed`, nada na fila) |
| idem + `forceReprocess: true` | reativar (`Queued`) |
| `Completed`, arquivo sumiu do disco | re-enfileirar |
| `Completed`, `SourceHash` ≠ `Episode.FileHash` (stale) | re-enfileirar |
| `Completed`, `Episode.FileHash` nulo/vazio + arquivo existente | skip (hash nulo = não-stale, coerente com `EpisodeProcessStatusMapper`) |
| `Failed`/`Cancelled` | reativar como hoje |
| `Queued`/`Processing` | no-op como hoje |

**Restrição crítica de concorrência:** resolução de skip (consultas ao
repositório, `File.Exists`, comparação de hashes) ocorre **FORA** do
`lock (_gate)`. Dentro do lock, apenas mutação de estado/canal (o bloco atual
de reativação/insert já é correto e permanece lá).

## Arquivos Alvo (fileScope)

1. `src/CATRA.Core/Interfaces/IProcessingQueueService.cs` — nova assinatura
   `Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile, bool forceReprocess = false)`
   + XML docs descrevendo skip e `forceReprocess`.
2. `src/CATRA.Services/Processing/ProcessingQueueService.cs` — lógica de skip.
3. `tests/CATRA.Services.Tests/Processing/ProcessingQueueServiceTests.cs` — novos testes.
4. `tests/CATRA.Services.Tests/Processing/ProcessingFakes.cs` — atualizar
   `FakeProcessingQueue.EnqueueAsync` para a nova assinatura (gravar flag).
5. `tests/CATRA.UI.Tests/Fakes.cs` — atualizar
   `FakeProcessingQueueService.EnqueueAsync` para a nova assinatura (gravar flag).

**NÃO tocar:** `src/CATRA.Services/Processing/SlidingWindowService.cs` e
demais callers — usam o default `forceReprocess: false` via parâmetro opcional.

## Passos Concretos

1. **Explorar contexto:** confirmar comportamento atual de `EnqueueAsync`
   (tudo dentro de `lock (_gate)`; `FindJob` → no-op para `Queued`/`Processing`,
   reativação in-place para terminais). Confirmar que
   `_processedFiles` (`IProcessedFileRepository`) e `_episodes`
   (`IEpisodeRepository`) já são dependências do serviço — nenhuma mudança no
   construtor. Confirmar `GetByEpisodeAndProfile(episodeId, profile)` na
   interface do repositório (já existe).

2. **Interface (`IProcessingQueueService.cs`):** alterar assinatura para
   `Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile, bool forceReprocess = false)`.
   Atualizar XML doc: job `Completed` com `ProcessedFile` válido (arquivo em
   disco + `SourceHash` == hash atual da fonte; hash da fonte nulo/vazio conta
   como válido) é pulado, exceto `forceReprocess: true`. `Failed`/`Cancelled`
   continuam reativados; ativos continuam no-op.

3. **Serviço (`ProcessingQueueService.EnqueueAsync`):** reestruturar em duas fases:
   - **Fase 1 — pré-lock (por episódio):** localizar job existente
     (`FindJob`). Se `existing is null` ou status ≠ `Completed` ou
     `forceReprocess == true` → marcar para enfileirar normalmente. Se
     `Completed` e `!forceReprocess`: resolver skip com
     `_processedFiles.GetByEpisodeAndProfile(episodeId, profile)`,
     `_episodes.GetById(episodeId)`, `File.Exists(processed.FilePath)` e
     comparação de hashes. Regra do hash:
     `string.IsNullOrEmpty(episode.FileHash)` OU
     `episode.FileHash == processed.SourceHash` → válido (skip); senão stale
     (re-enfileirar). `processed == null` ou arquivo ausente → re-enfileirar.
   - **Fase 2 — dentro do `lock (_gate)`:** apenas mutação
     (reativação de terminal / insert de novo / `_queuedJobs.Add` /
     `_channel.Writer.TryWrite`), exatamente como hoje para os jobs não-skippados.
   - **Edge cases:**
     - `processed.FilePath` nulo/vazio → `File.Exists` retorna false → re-enfileirar.
     - Job `Completed` mas `GetById` do episódio retorna null → tratar como
       não-válido → re-enfileirar (worker fará FailJob depois, como já ocorre).
     - TOCTOU benigno: status pode mudar entre fase 1 e fase 2 (ex.:
       `ClearQueueAsync` concorrente). Aceitável — o worker re-lê o job do DB
       antes de processar (comportamento já existente).

4. **Fakes:** atualizar `FakeProcessingQueue`
   (`tests/CATRA.Services.Tests/Processing/ProcessingFakes.cs`) e
   `FakeProcessingQueueService` (`tests/CATRA.UI.Tests/Fakes.cs`) para a nova
   assinatura com default `false`; gravar a flag nos registros de chamada
   (ex.: tupla `(List<int> Ids, ProcessProfile Profile, bool ForceReprocess)`).
   Não alterar comportamento existente dos testes que usam os fakes.

5. **Testes (`ProcessingQueueServiceTests.cs`):** adicionar casos cobrindo os
   6 ramos da tabela acima, usando fakes de repositório e stub de filesystem
   (abstração injetável ou arquivo real em `Path.GetTempPath()` — sem I/O de
   rede; ver como os testes existentes do serviço já lidam com
   `FakeProcessingPipeline.WriteOutputFile`). Casos mínimos:
   - `Completed` + `ProcessedFile` válido → status segue `Completed`,
     `QueuedJobs` vazio, canal não recebe o job.
   - idem com `forceReprocess: true` → status vira `Queued`.
   - `Completed` + arquivo inexistente → re-enfileirado.
   - `Completed` + hashes divergentes → re-enfileirado.
   - `Completed` + `Episode.FileHash` nulo e arquivo existente → skip.
   - `Failed`/`Cancelled` → reativados (regressão do comportamento atual).
   - `Queued`/`Processing` → no-op (regressão).

6. **Build gate:** `dotnet build CATRA.sln --no-restore` (0w/0e) +
   `dotnet test`. Se build falhar, corrigir antes de concluir.

## Critérios de Aceite

- [ ] `EnqueueAsync` com episódio `Completed` + `ProcessedFile` válido → job
      permanece `Completed`, nada entra na fila.
- [ ] Mesma situação com `forceReprocess: true` → job reativado (`Queued`).
- [ ] Job `Completed` cujo arquivo sumiu do disco → re-enfileirado.
- [ ] Job `Completed` com hash da fonte divergente (stale) → re-enfileirado.
- [ ] Job `Completed` com `Episode.FileHash` nulo/vazio e arquivo existente → skip.
- [ ] Jobs `Failed`/`Cancelled` → reativados como hoje.
- [ ] Jobs `Queued`/`Processing` → no-op.
- [ ] I/O (`File.Exists`/consulta de hash) fora do `lock (_gate)`; dentro do
      lock apenas mutação de estado/canal.
- [ ] Todos os fakes de `IProcessingQueueService` atualizados para a nova
      assinatura; nenhum outro implementador quebrado.
- [ ] Callers existentes compilam sem alteração (parâmetro opcional).
- [ ] `dotnet build CATRA.sln --no-restore` passa com 0w/0e.
- [ ] `dotnet test` passa (testes novos + regressão).

## Dependências

Nenhuma (primeira subtask da feature).

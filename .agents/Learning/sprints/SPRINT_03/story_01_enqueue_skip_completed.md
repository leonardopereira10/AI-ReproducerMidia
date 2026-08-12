# Story 01 — EnqueueAsync pula episódios já processados (skip + forceReprocess)

**Tipo:** dev
**Feature:** Retomada de Fila sem Reprocessamento (spec: `.agents/specs/resume_queue_no_reprocess_plan.md`)

## Descrição

`ProcessingQueueService.EnqueueAsync` hoje reativa qualquer job terminal
(Completed/Failed/Cancelled), causando reprocessamento indevido de episódios
já processados quando a janela é reaberta (restart/troca de série).

Implementar decisão de **skip**: job `Completed` cujo par (episode, profile)
tenha `ProcessedFile` válido — arquivo existe em disco E `ProcessedFile.SourceHash`
confere com `Episode.FileHash` atual — **não** é re-enfileirado. A nova flag
`forceReprocess = false` (parâmetro opcional na interface) permite bypassar o
skip para uso futuro (UI de reprocessamento forçado).

Regras de negócio:
- `Completed` + `ProcessedFile` válido → **skip** (exceto `forceReprocess: true`).
- `Episode.FileHash` nulo/vazio → tratado como não-stale (coerente com
  `EpisodeProcessStatusMapper`): se o arquivo processado existe, é skip.
- `ProcessedFile.SourceHash` divergente do hash atual da fonte (stale) → re-enfileirar.
- Arquivo processado sumiu do disco → re-enfileirar.
- Jobs `Failed`/`Cancelled` → continuam sendo reativados (retomada legítima).
- Jobs ativos (`Queued`/`Processing`) → continuam no-op.
- **Lock + I/O:** resolução de skip (`File.Exists`/hash) ocorre FORA do
  `lock (_gate)`; dentro do lock apenas mutação de estado/canal.

## Escopo

### Arquivos a Modificar
- `src/CATRA.Core/Interfaces/IProcessingQueueService.cs` — nova assinatura
  `Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile, bool forceReprocess = false)`
  + XML docs descrevendo skip e forceReprocess.
- `src/CATRA.Services/Processing/ProcessingQueueService.cs` — lógica de skip
  (consulta `IProcessedFileRepository.GetByEpisodeAndProfile`, `File.Exists`,
  comparação de hashes) fora do lock; mutação dentro do lock.
- `tests/CATRA.Services.Tests/Processing/ProcessingQueueServiceTests.cs` — novos testes.
- `tests/CATRA.Services.Tests/Processing/ProcessingFakes.cs` — fake de
  `IProcessingQueueService` atualizado para a nova assinatura.
- `tests/CATRA.UI.Tests/Fakes.cs` — idem.

### Arquivos NÃO tocar
- `src/CATRA.Services/Processing/SlidingWindowService.cs` — fora de escopo;
  callers existentes usam o default `forceReprocess: false`.

## Critérios de Aceite
- [ ] `EnqueueAsync` com episódio `Completed` + `ProcessedFile` válido → job
      permanece `Completed`, nada entra na fila.
- [ ] Mesma situação com `forceReprocess: true` → job reativado (`Queued`).
- [ ] Job `Completed` cujo arquivo sumiu do disco → re-enfileirado.
- [ ] Job `Completed` com hash da fonte divergente (stale) → re-enfileirado.
- [ ] Job `Completed` com `Episode.FileHash` nulo/vazio e arquivo existente → skip.
- [ ] Jobs `Failed`/`Cancelled` → reativados como hoje.
- [ ] Jobs `Queued`/`Processing` → no-op.
- [ ] I/O (`File.Exists`/hash) fora do `lock (_gate)`.
- [ ] Build passa (`dotnet build`, 0w/0e) e testes passam (`dotnet test`).

## Requisitos Técnicos
- Nomes em inglês; manter padrão de constructor injection (`IProcessedFileRepository`
  já é dependência do serviço).
- Atualizar TODOS os fakes que implementam `IProcessingQueueService` (a assinatura
  da interface muda).
- Testes devem cobrir os 6 ramos acima (usar fakes de filesystem/repositório,
  sem I/O real nos testes unitários).

## Dependências
- Nenhuma (primeira story da feature).

## Notas
- Sem mudança de schema; nenhuma coluna nova.
- `File.Exists` em caminho UNC pode ser lento — aceitável (checagem única por
  enqueue, fora do caminho quente de decode).

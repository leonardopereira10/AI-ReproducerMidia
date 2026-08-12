# Plano de Execução — Retomada de Fila sem Reprocessamento (SPRINT_03)

## Objetivo
Permitir retomar o processamento da fila sem reprocessar episódios já processados:
um episódio com job `Completed` e `ProcessedFile` válido (arquivo existe em disco e
`SourceHash` igual ao `Episode.FileHash` atual) deve ser ignorado ao re-enfileirar.

## Contexto Técnico
- `ProcessingQueueService.EnqueueAsync` hoje REATIVA qualquer job terminal
  (Completed/Failed/Cancelled), resetando para `Queued` — causa reprocessamento
  indevido quando `SlidingWindowService.StartWindowAsync` reabre a janela após
  restart ou troca de série.
- Jobs `Queued` que sobraram no banco após shutdown limpo/crash nunca são
  retomados: `StartAsync` só recupera `Processing → Failed` (`RecoverCrashedJobs`).
- `ProcessedFile.SourceHash` vs `Episode.FileHash` já sustenta o status `Stale`
  (RN: fonte alterada exige reprocessamento).

## Escopo
1. `EnqueueAsync(episodeIds, profile, forceReprocess = false)`:
   - job `Completed` + `ProcessedFile` do par (episode, profile) existe +
     arquivo existe em disco + hash da fonte confere → **skip** (não re-enfileirar),
     a menos que `forceReprocess == true`.
   - job `Failed`/`Cancelled` continua sendo reativado (retomada legítima).
   - job ativo (`Queued`/`Processing`) continua no-op.
2. `StartAsync`: após `RecoverCrashedJobs`, re-enfileirar jobs `Queued` órfãos
   persistidos no banco (retomada pós-restart).
3. Atualizar XML docs de `IProcessingQueueService` e `ProcessingQueueService`.
4. Testes unitários cobrindo os novos comportamentos (inclui atualizar fakes
   que implementam `IProcessingQueueService`: `tests/CATRA.Services.Tests/Processing/ProcessingFakes.cs`,
   `tests/CATRA.UI.Tests/Fakes.cs`).

## Decisões de Design (feedback PO)
- **Lock + I/O:** resolução de skip (File.Exists/hash) deve ocorrer FORA do
  `lock (_gate)`; dentro do lock apenas mutação de estado/canal.
- **Hash nulo:** `Episode.FileHash` nulo/vazio → tratado como não-stale
  (coerente com `EpisodeProcessStatusMapper`): se o arquivo processado existe,
  é skip. Se `ProcessedFile.SourceHash` diverge do hash atual da fonte,
  re-enfileirar.

## Fora de Escopo
- Mudanças em `SlidingWindowService` (dedup já ocorre via `EnqueueAsync`).
- Retentativa automática de jobs `Failed` por erro real (não-crash).
- UI de "reprocessar forçado" (flag fica disponível para uso futuro).
- Migração de schema (nenhuma coluna nova).

## Critérios de Aceite
- [ ] `EnqueueAsync` com episódio já `Completed` + arquivo válido → job permanece
      `Completed`, nada entra na fila.
- [ ] Mesma situação com `forceReprocess: true` → job reativado (`Queued`).
- [ ] Job `Completed` cujo arquivo sumiu do disco → re-enfileirado.
- [ ] Job `Completed` com hash da fonte divergente (stale) → re-enfileirado.
- [ ] Jobs `Failed`/`Cancelled` → reativados como hoje.
- [ ] Após `StartAsync`, jobs `Queued` persistidos no banco voltam para a fila.
- [ ] `dotnet build` + `dotnet test` verdes (gate).

## Riscos
- Corrida entre retomada em `StartAsync` e `StartWindowAsync` → mitigado pelo
  dedup já existente para jobs ativos.
- `File.Exists` em caminho remoto/UNC pode ser lento → aceitável (checagem única
  por enqueue, fora do caminho quente de decode).
- Tests existentes que esperam reativação de job `Completed` podem quebrar →
  QA deve revisar suíte atual.

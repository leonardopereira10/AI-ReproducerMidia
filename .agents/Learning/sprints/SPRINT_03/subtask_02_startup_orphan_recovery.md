# Subtask 02: Retomada de jobs Queued órfãos no StartAsync (pós-restart)

**Story:** story_02_startup_orphan_recovery.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Implementar a retomada de jobs `Queued` persistidos no banco após shutdown
limpo ou crash. Hoje `ProcessingQueueService.StartAsync` só executa
`RecoverCrashedJobs` (`Processing → Failed`); jobs `Queued` órfãos nunca
voltam para a fila em memória e ficam presos para sempre.

Alteração: em `StartAsync`, **após** `RecoverCrashedJobs`, buscar no
repositório todos os jobs com `Status == JobStatus.Queued` e re-enfileirá-los
na lista `_queuedJobs` + `_channel` (dentro do `_gate`, mesmo padrão de
`EnqueueAsync`). A ordem é obrigatória: crash recovery primeiro (marca
`Processing` órfão como `Failed`), retomada de `Queued` depois — assim um job
não é retomado e falhado ao mesmo tempo. Jobs já ativos na fila em memória não
devem ser duplicados (dedup existente para `Queued`/`Processing` = no-op
mitiga corrida com `StartWindowAsync`, conforme nota de risco do plano).
Atualizar os XML docs de `IProcessingQueueService` e `ProcessingQueueService`
para documentar o novo comportamento de retomada.

## Arquivos Alvo (fileScope)

- `src/CATRA.Services/Processing/ProcessingQueueService.cs` — `StartAsync`
  (chamar recuperação de `Queued` órfãos após `RecoverCrashedJobs`), método
  privado novo (ex.: `RecoverOrphanedQueuedJobs`), XML docs da classe/método.
- `src/CATRA.Core/Interfaces/IProcessingQueueService.cs` — XML doc de
  `StartAsync` descrevendo retomada de `Queued` órfãos (além do já feito na
  Story 01, se necessário).
- `tests/CATRA.Services.Tests/Processing/ProcessingQueueServiceTests.cs` —
  novos testes da retomada.

**NÃO tocar:**
- `src/CATRA.Services/Processing/SlidingWindowService.cs` — fora de escopo.
- Lógica de skip da Story 01 no `EnqueueAsync` — não alterar.

## Passos

1. Ler `ProcessingQueueService.cs` (contexto: `StartAsync`, `RecoverCrashedJobs`,
   `EnqueueAsync`, `_gate`/`_queuedJobs`/`_channel`) e
   `IProcessJobRepository` (`GetAll()` via `IRepository<ProcessJob>`) para
   entender padrões existentes.
2. Criar método privado em `ProcessingQueueService` (ex.:
   `RecoverOrphanedQueuedJobs`): via `_jobs.GetAll()`, filtrar
   `Status == JobStatus.Queued`, ordenar por `CreatedAt` ascendente (preservar
   ordem original da fila), e para cada job: dedup contra `_queuedJobs`/job
   corrente já ativo (no-op se já presente), senão `_queuedJobs.Add(job)` +
   `_channel.Writer.TryWrite(job)`. Executar dentro do `lock (_gate)`,
   replicando o padrão de escrita sob lock do `EnqueueAsync`.
3. Em `StartAsync`: invocar o novo método **imediatamente após**
   `RecoverCrashedJobs()` e antes de iniciar o worker (`Task.Run(LoopAsync)`),
   ainda dentro do lock existente. Não alterar idempotência
   (`_workerTask is not null → return`).
4. Atualizar XML docs:
   - `IProcessingQueueService.StartAsync` — documentar ordem: crash recovery
     (`Processing → Failed`) e depois retomada de `Queued` persistidos para a
     fila em memória.
   - `ProcessingQueueService` (classe) e método novo — documentar retomada
     pós-restart e dedup.
5. Testes novos em `ProcessingQueueServiceTests.cs` (xUnit + FluentAssertions,
   fakes de `ProcessingFakes.cs`; simular banco com job `Queued` persistido e
   validar estado da fila após `StartAsync`):
   - `StartAsync_RecoversPersistedQueuedJobs_BackIntoMemoryQueue` — job
     `Queued` persistido antes do start → presente em `QueuedJobs` após
     `StartAsync` (status permanece `Queued`, sem reprocesso de status).
   - `StartAsync_RecoversQueuedJobs_AfterCrashRecovery` — job `Processing`
     persistido vira `Failed` E job `Queued` persistido é retomado na mesma
     chamada (ordem correta).
   - `StartAsync_NoQueuedJobs_QueueRemainsEmpty` — banco sem `Queued` →
     fila vazia, sem erro; crash recovery intacta.
   - Idempotência de `StartAsync` preservada (2ª chamada não duplica jobs na
     fila).
6. Confirmar regressão: testes existentes
   `CrashRecovery_MarksProcessingJobs_Failed` e
   `StartAsync_RunsCrashRecovery_AndIsIdempotent` continuam verdes.
7. Build gate: `dotnet build CATRA.sln --no-restore` (0 warnings, 0 erros) +
   `dotnet test` (100% verde).

## Critérios de Aceite

- [ ] Após `StartAsync`, jobs `Queued` persistidos no banco voltam para a fila em memória (`QueuedJobs`).
- [ ] Retomada de `Queued` executa DEPOIS de `RecoverCrashedJobs` (ordem verificada por teste).
- [ ] Recuperação `Processing → Failed` permanece intacta (testes existentes verdes).
- [ ] Jobs já ativos na fila em memória não são duplicados na retomada.
- [ ] XML docs de `IProcessingQueueService.StartAsync` e `ProcessingQueueService` refletem a retomada.
- [ ] Testes novos cobrem: retomada de `Queued` persistido, ordem pós-crash-recovery, caso sem `Queued`, idempotência.
- [ ] `dotnet build CATRA.sln --no-restore` 0w/0e e `dotnet test` 100% verde.
- [ ] Nenhuma alteração em `SlidingWindowService.cs` ou na lógica de skip da Story 01.

## Dependências

- subtask_01 (skip de `Completed` no `EnqueueAsync`) — mesma classe
  (`ProcessingQueueService`); deve estar concluída para evitar conflito de
  edição e para a suíte de testes estar estável.

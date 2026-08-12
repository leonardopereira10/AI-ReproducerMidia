# Story 02 — Retomada de jobs Queued órfãos no StartAsync (pós-restart)

**Tipo:** dev
**Feature:** Retomada de Fila sem Reprocessamento (spec: `.agents/specs/resume_queue_no_reprocess_plan.md`)

## Descrição

Jobs `Queued` que ficaram persistidos no banco após shutdown limpo ou crash
nunca são retomados: `ProcessingQueueService.StartAsync` hoje só recupera
jobs `Processing → Failed` via `RecoverCrashedJobs`.

Implementar: em `StartAsync`, **após** `RecoverCrashedJobs`, localizar jobs
`Queued` órfãos persistidos no banco e re-enfileirá-los na fila em memória
(retomada pós-restart). Atualizar os XML docs de `IProcessingQueueService`
e `ProcessingQueueService` para documentar o comportamento de retomada.

## Escopo

### Arquivos a Modificar
- `src/CATRA.Services/Processing/ProcessingQueueService.cs` — `StartAsync`:
  após `RecoverCrashedJobs`, buscar jobs `Queued` persistidos e enfileirar;
  XML docs do método/classe atualizados.
- `src/CATRA.Core/Interfaces/IProcessingQueueService.cs` — XML doc de
  `StartAsync` descrevendo a retomada de `Queued` órfãos (se necessário além
  do já feito na Story 01).
- `tests/CATRA.Services.Tests/Processing/ProcessingQueueServiceTests.cs` — novos testes.

### Arquivos NÃO tocar
- `src/CATRA.Services/Processing/SlidingWindowService.cs` — fora de escopo.
- Lógica de skip da Story 01 — não alterar.

## Critérios de Aceite
- [ ] Após `StartAsync`, jobs `Queued` persistidos no banco voltam para a
      fila em memória.
- [ ] Recuperação `Processing → Failed` (`RecoverCrashedJobs`) permanece intacta.
- [ ] Retomada de `Queued` executa DEPOIS de `RecoverCrashedJobs`.
- [ ] Jobs já ativos na fila em memória não são duplicados (dedup existente).
- [ ] XML docs de `IProcessingQueueService` e `ProcessingQueueService`
      refletem os novos comportamentos.
- [ ] Build passa (`dotnet build`, 0w/0e) e testes passam (`dotnet test`).

## Requisitos Técnicos
- Nomes em inglês; manter padrões existentes do serviço.
- Teste deve simular banco com job `Queued` persistido e validar o estado
  da fila após `StartAsync`.

## Dependências
- Story 01 (interface já alterada e suíte de testes estável).

## Notas
- Risco do plano: corrida entre retomada em `StartAsync` e `StartWindowAsync`
  → mitigada pelo dedup já existente para jobs ativos (`Queued`/`Processing` = no-op).

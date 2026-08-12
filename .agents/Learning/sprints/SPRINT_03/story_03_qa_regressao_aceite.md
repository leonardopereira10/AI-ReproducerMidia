# Story 03 — QA: regressão da suíte e validação dos critérios de aceite

**Tipo:** qa
**Feature:** Retomada de Fila sem Reprocessamento (spec: `.agents/specs/resume_queue_no_reprocess_plan.md`)

## Descrição

Validação QA da feature completa (Stories 01 e 02): revisar a suíte de testes
existente quanto a regressões de expectativa (testes antigos que esperavam
REATIVAÇÃO de jobs `Completed` no `EnqueueAsync` podem precisar de ajuste de
expectativa), executar o gate build+test e verificar item a item os critérios
de aceite do plano.

## Escopo
- Revisar `tests/CATRA.Services.Tests/Processing/ProcessingQueueServiceTests.cs`
  e demais testes que interagem com `IProcessingQueueService` buscando
  expectativas quebradas pela nova semântica de skip.
- Verificar cobertura dos novos comportamentos (skip, forceReprocess, arquivo
  ausente, hash divergente, retomada pós-restart).
- Executar gate: `dotnet build` (0w/0e) + `dotnet test` (100% verde).

## Critérios de Aceite
- [ ] Nenhum teste existente quebrado sem ajuste consciente de expectativa.
- [ ] Todos os critérios de aceite do plano verificados (spec, seção
      "Critérios de Aceite"), incluindo:
      - `Completed` + arquivo válido → permanece `Completed`.
      - `forceReprocess: true` → reativado.
      - Arquivo ausente ou hash divergente → re-enfileirado.
      - `Failed`/`Cancelled` → reativados.
      - `Queued` persistidos retomados após `StartAsync`.
- [ ] `dotnet build` + `dotnet test` verdes (gate).
- [ ] Report de QA lista qualquer desvio ou risco residual.

## Requisitos Técnicos
- Falhas pré-existentes conhecidas devem ser identificadas como tal (não
  atribuídas à feature).

## Dependências
- Story 01 e Story 02 concluídas.

## Notas
- Risco mapeado no plano: "testes existentes que esperam reativação de job
  `Completed` podem quebrar" — esta story cobre explicitamente essa revisão.

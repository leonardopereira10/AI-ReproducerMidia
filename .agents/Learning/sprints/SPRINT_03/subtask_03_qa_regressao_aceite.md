# Subtask 03: QA — regressão da suíte e validação dos critérios de aceite

**Story:** story_03_qa_regressao_aceite.md
**Tipo:** qa
**Complexidade:** media
**Agente:** qa-tester-medio

## Descrição

Validação QA da feature "Retomada de Fila sem Reprocessamento" (spec:
`.agents/specs/resume_queue_no_reprocess_plan.md`). Revisar a suíte de testes
existente quanto a regressões de expectativa causadas pela nova semântica de
skip do `EnqueueAsync` (Story 01) e pela recuperação de jobs órfãos no startup
(Story 02), ajustar expectativas quebradas de forma consciente, verificar a
cobertura dos novos comportamentos e executar o gate build+test. Produzir
report de QA com desvios e riscos residuais.

## Arquivos Alvo (fileScope)

- `tests/CATRA.Services.Tests/Processing/ProcessingQueueServiceTests.cs`
- `tests/CATRA.Services.Tests/Processing/ProcessingFakes.cs` (se fixture precisar de ajuste)
- `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs` (regressão indireta)
- `src/CATRA.Services/Processing/ProcessingQueueService.cs` (leitura para conferência de semântica)
- `.agents/specs/resume_queue_no_reprocess_plan.md` (checklist de critérios de aceite)

## Passos

1. Identificar testes existentes que esperam **REATIVAÇÃO de job `Completed`**
   no `EnqueueAsync`. Candidato principal:
   `Enqueue_ReactivatesTerminalJob_InPlace` (`ProcessingQueueServiceTests.cs:356`)
   — enfileira episódio já `Completed` sem `forceReprocess` e espera
   reprocessamento (`CallCount == 2`). Com a nova semântica de skip, este teste
   deve falhar por design → ajustar expectativa (job permanece `Completed`,
   `CallCount == 1`) ou converter em teste de `forceReprocess: true`.
2. Varrer os demais usos de `EnqueueAsync` no diretório
   `tests/CATRA.Services.Tests/Processing/` (incl. `Enqueue_DuplicateActiveJob_IsNoOp`,
   `ClearQueue_CancelsQueuedJobs_ButNotActive`, `ProgressEvents_Propagate_FromPipeline`)
   buscando expectativas incompatíveis com skip/recovery; classificar cada falha
   como regressão real vs. expectativa desatualizada.
3. Verificar/validar cobertura dos novos comportamentos da Story 01:
   - `Completed` + arquivo válido + hash igual → permanece `Completed` (skip, sem reprocesso).
   - `forceReprocess: true` → reativado mesmo `Completed`.
   - Arquivo processado ausente → re-enfileirado.
   - Hash divergente → re-enfileirado.
   - `Failed`/`Cancelled` → reativados.
4. Verificar/validar cobertura dos novos comportamentos da Story 02:
   - `Queued` persistidos são retomados após `StartAsync` (retomada pós-restart).
   - `Processing` órfãos marcados `Failed` (`RecoverCrashedJobs` — testes
     `CrashRecovery_MarksProcessingJobs_Failed` e
     `StartAsync_RunsCrashRecovery_AndIsIdempotent` já existentes).
5. Ajustar ou adicionar testes faltantes (xUnit + FluentAssertions, mocks/fakes
   de `ProcessingFakes.cs`; sem dependências externas reais).
6. Build gate: `dotnet build CATRA.sln --no-restore` (0 warnings, 0 erros) +
   `dotnet test` (100% verde).
7. Classificar falhas pré-existentes conhecidas como pré-existentes (não
   atribuir à feature) e registrá-las no report.
8. Emitir report de QA: checklist item a item dos "Critérios de Aceite" do
   plano, desvios encontrados e riscos residuais.

## Critérios de Aceite

- [ ] Nenhum teste existente quebrado sem ajuste consciente de expectativa documentado.
- [ ] `Enqueue_ReactivatesTerminalJob_InPlace` revisado: expectativa alinhada à semântica de skip (ou convertido para `forceReprocess`).
- [ ] Cobertura confirmada: skip de `Completed`, `forceReprocess`, arquivo ausente, hash divergente, reativação de `Failed`/`Cancelled`, retomada de `Queued` pós-`StartAsync`.
- [ ] Todos os critérios de aceite da spec verificados item a item.
- [ ] `dotnet build` 0w/0e e `dotnet test` 100% verde.
- [ ] Report de QA entregue com desvios e riscos residuais; falhas pré-existentes identificadas como tal.

## Dependências

- subtask_01 (skip de `Completed` no `EnqueueAsync`) — concluída.
- subtask_02 (recuperação de jobs órfãos no startup) — concluída.

# Ordem de Execução — Sprint Atual (Feature: Retomada de Fila sem Reprocessamento)

**Plano de referência:** `.agents/specs/resume_queue_no_reprocess_plan.md`
**Validado por:** Product Owner (specs, subtasks e código-fonte conferidos)

## Tabela de Execução

| Ordem | Subtask | Agente | Dependência |
|-------|---------|--------|-------------|
| 1 | subtask_01_enqueue_skip_completed.md | developer-medio | Nenhuma |
| 2 | subtask_02_startup_orphan_recovery.md | developer-medio | subtask_01 |
| 3 | subtask_03_qa_regressao_aceite.md | qa-tester-medio | subtask_01, subtask_02 |

## Justificativa da Ordem

1. **subtask_01** primeiro: altera a assinatura de `IProcessingQueueService.EnqueueAsync`
   (parâmetro opcional `forceReprocess`) e introduz a semântica de skip. Nenhuma
   dependência; é a base da feature.
2. **subtask_02** em seguida: edita a mesma classe (`ProcessingQueueService.StartAsync`)
   e o mesmo arquivo de testes — depende da subtask_01 concluída para evitar conflito
   de edição e suíte instável.
3. **subtask_03** por último: QA só é válido com as duas implementações concluídas;
   cobre o risco mapeado no plano de testes antigos que esperam reativação de
   `Completed` (ex.: `Enqueue_ReactivatesTerminalJob_InPlace`).

## Ajustes Aplicados/Recomendados

- **subtask_01:** passo 5 oferece "arquivo real em `Path.GetTempPath()`" como opção,
  mas a story exige "sem I/O real nos testes unitários" → priorizar stub/abstração de
  filesystem injetável; temp file apenas como fallback.
- **subtask_03:** varredura de regressão deve incluir também `tests/CATRA.UI.Tests`
  (usa `FakeProcessingQueueService` cuja assinatura muda na subtask_01), além do
  escopo já listado em `CATRA.Services.Tests`.
- Nenhum ajuste estrutural necessário: escopos, critérios de aceite e dependências
  estão consistentes com o plano e com o código atual (verificado: dependências do
  construtor já existem, `GetByEpisodeAndProfile` existe, fakes nos locais citados,
  teste da linha 356 confirmado).

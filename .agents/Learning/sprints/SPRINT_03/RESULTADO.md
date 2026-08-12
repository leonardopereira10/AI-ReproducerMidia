# SPRINT_03 — Retomada de Fila sem Reprocessamento

**Período:** 2026-08-12
**Status:** ✅ CONCLUÍDA — validação final APROVADA

## Resumo

Feature que permite retomar a fila de processamento após restart/troca de série
sem reprocessar episódios já processados: `EnqueueAsync` pula jobs `Completed`
com `ProcessedFile` válido (arquivo em disco + `SourceHash` confere com
`Episode.FileHash`), com bypass via flag opcional `forceReprocess`, e
`StartAsync` retoma jobs `Queued` órfãos persistidos no banco pós-restart
(após `RecoverCrashedJobs`).

**Spec:** `.agents/Learning/sprints/SPRINT_03/resume_queue_no_reprocess_plan.md`
(cópia de `.agents/specs/resume_queue_no_reprocess_plan.md`)

## Subtasks (3/3)

| ST | Título | Agente | Commit | Retries |
|----|--------|--------|--------|---------|
| 01 | EnqueueAsync: skip de `Completed` com `ProcessedFile` válido + `forceReprocess` | developer-medio | b3bbdc6 → 9481473 | 1 (rejeição QA: teste flaky → fix no retry) |
| 02 | StartAsync retoma jobs `Queued` órfãos pós-restart | developer-medio | e2b78a4 | 0 |
| 03 | QA: regressão da suíte + validação dos critérios de aceite | qa-tester-medio | — (report) | 0 |

**Nota:** commit b3bbdc6 = 1ª tentativa da subtask_01; rejeitada no QA por teste
flaky (worker assíncrono não sincronizado). Retry 9481473 corrige com TCS de
sincronização e passa no gate.

## Testes

- **Total:** 555 — **550 passam**
- **Build:** `dotnet build CATRA.sln --no-restore` — 0 warnings / 0 errors
- **Falhas (5, baseline pré-existente, FORA de escopo):**
  - Casting AVI ×1 (pré-existente no HEAD)
  - Settings seed `local_target_fps` ×4 (seed do banco diverge da expectativa dos testes — divergência já documentada em sprints anteriores, sem relação com a feature)
- **Regressão consciente:** `Enqueue_ReactivatesTerminalJob_InPlace` — expectativa
  ajustada à nova semântica de skip (job permanece `Completed` sem
  `forceReprocess`), conforme risco mapeado no plano e executado na subtask_03.

## Critérios de Aceite Verificados (plano)

- ✅ `EnqueueAsync` com episódio `Completed` + `ProcessedFile` válido → job
      permanece `Completed`, nada entra na fila
- ✅ Mesma situação com `forceReprocess: true` → job reativado (`Queued`)
- ✅ Job `Completed` com arquivo sumido do disco → re-enfileirado
- ✅ Job `Completed` com hash da fonte divergente (stale) → re-enfileirado
- ✅ Job `Completed` com `Episode.FileHash` nulo/vazio + arquivo existente → skip
      (hash nulo = não-stale, coerente com `EpisodeProcessStatusMapper`)
- ✅ Jobs `Failed`/`Cancelled` → reativados como antes
- ✅ Jobs `Queued`/`Processing` → no-op
- ✅ Após `StartAsync`, jobs `Queued` persistidos voltam para a fila em memória,
      DEPOIS de `RecoverCrashedJobs` (ordem verificada por teste); idempotência preservada
- ✅ I/O (`File.Exists`/hash) fora do `lock (_gate)`; dentro do lock só mutação
- ✅ Fakes (`ProcessingFakes.cs`, `UI.Tests/Fakes.cs`) atualizados para a nova
      assinatura; callers existentes compilam sem alteração (parâmetro opcional)
- ✅ Gate `dotnet build` 0w/0e + `dotnet test` verde (dentro do escopo)

## Lições

- **Injeção `Func<string,bool> fileExists`:** `File.Exists` estático impede teste
  unitário sem I/O real. Injetar o predicate no construtor (`p => File.Exists(p)`
  em produção, stub nos testes) resolveu a exigência da story de "sem I/O real
  nos testes unitários" sem abstração de filesystem pesada.
- **I/O fora de lock:** resolução de skip (consultas de repositório, `File.Exists`,
  comparação de hashes) ocorre ANTES do `lock (_gate)`; dentro do lock apenas
  mutação de estado/canal. Evita latência de disco segurando o gate da fila
  (relevante p/ caminhos UNC/remotos).
- **TCS 'entered' p/ testes determinísticos:** teste flaky da subtask_01 vinha de
  assert contra worker assíncrono sem ponto de sincronização. Um
  `TaskCompletionSource` sinalizado quando o worker entra no ponto de interesse
  ("entered") permite aguardar o estado exato antes de assertar — padrão a reusar
  em qualquer teste de consumidor de canal/loop assíncrono.
- **Rejeição QA ≠ retrabalho grande:** o retry consumiu apenas o fix de
  sincronização do teste; o código de produção da subtask_01 já estava correto.
- **TOCTOU benigno documentado:** status pode mudar entre a fase pré-lock e a
  mutação; aceitável porque o worker re-lê o job do DB antes de processar.

## Riscos Residuais

- Corrida `StartAsync` × `SlidingWindowService.StartWindowAsync` — mitigada pelo
  dedup existente (job ativo = no-op); não reproduzível em teste de integração real.
- `File.Exists` em caminho UNC pode ser lento — aceitável: checagem única por
  enqueue, fora do caminho quente de decode.
- UI de "reprocessar forçado" não implementada — flag `forceReprocess` disponível
  para uso futuro (fora de escopo por decisão de design).
- 5 falhas baseline (Casting AVI ×1, Settings seed fps ×4) permanecem — precisam
  de sprint própria (seed do banco vs expectativa dos testes).
- Validação real pós-restart (app com fila persistida fechando/abrindo) é manual —
  comportamento coberto apenas por testes unitários com repositórios fake.

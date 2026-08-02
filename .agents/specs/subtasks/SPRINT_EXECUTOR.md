# 🚀 CATRA — Sprint Executor Prompt

> Cole este prompt na sessão principal para executar a sprint em loop de subagents.
> Substitua `{FASE}` por `1` ou `2` e `{START}` pela primeira subtask desejada.

---

## PROMPT (copiar daqui)

```
🚀 execute the sprint CATRA — Fase {FASE}

## CONTEXTO

Projeto: CATRA — Reprodutor de Mídia Desktop
Spec principal: .agents/specs/catra-media-player.md
Backlog: .agents/specs/subtasks/BACKLOG.md
Subtasks: .agents/specs/subtasks/ST-*.md
Solução: CATRA.sln (WPF .NET 8+, C# 12)
Build: dotnet build CATRA.sln
Test: dotnet test CATRA.sln
Native (Fase 2): powershell -File scripts/build-native.ps1

Stack: .NET 8+, C# 12, WPF, FFmpeg.AutoGen, Vortice.Windows, SQLite (sqlite-net-pcl),
CommunityToolkit.Mvvm, Rssdp, NAudio, xUnit, FluentAssertions.
Native (Fase 2): C++20, CMake, vcpkg, FSR 4 SDK, RIFE v4 (ONNX/DirectML), AMF SDK.

## INSTRUÇÕES DE EXECUÇÃO

Carregar skills: orchestration, auto-delegate, build-gate, complexity-eval, caveman.

### FASE 1 — Ordem de execução (respeitar dependências):

Lote 1 (single):    ST-01  [baixa]    → developer-baixo
Lote 2 (single):    ST-02  [media]    → developer-medio
Lote 3 (paralelo):  ST-03  [alta]     → developer-alto
                    ST-10  [baixa]    → developer-baixo
Lote 4 (paralelo):  ST-04  [alta]     → developer-alto
                    ST-05  [critical] → developer-critical
Lote 5 (paralelo):  ST-06  [alta]     → developer-alto
                    ST-09  [media]    → developer-medio
Lote 6 (paralelo):  ST-07  [media]    → developer-medio
                    ST-08  [alta]     → developer-alto
Lote 7 (single):    ST-11  [media]    → developer-medio

### FASE 2 — Ordem de execução:

Lote 1 (single):    ST-12  [alta]     → developer-alto
Lote 2 (paralelo):  ST-13  [critical] → developer-critical
                    ST-14  [critical] → developer-critical
Lote 3 (paralelo):  ST-15  [alta]     → developer-alto
                    ST-16  [alta]     → developer-alto
Lote 4 (single):    ST-17  [critical] → developer-critical
Lote 5 (single):    ST-18  [alta]     → developer-alto
Lote 6 (paralelo):  ST-19  [media]    → developer-medio
                    ST-20  [media]    → developer-medio
Lote 7 (paralelo):  ST-21  [baixa]    → developer-baixo
                    ST-22  [baixa]    → developer-baixo

### LOOP POR SUBTASK (para cada lote):

Para cada subtask no lote:

1. LER a subtask: .agents/specs/subtasks/ST-{nn}-{slug}.md
2. DELEGAR para o developer-{nivel} com:
   - Task: conteúdo COMPLETO da subtask (verbatim)
   - Contexto adicional: APENAS a spec principal se a subtask referenciar
   - PROIBIDO: passar outras subtasks, histórico, ou conversas de outros agentes
   - async: true
   - control: { enabled: true, needsAttentionAfterMs: 120000, activeNoticeAfterMs: 300000, activeNoticeAfterTurns: 15 }
   - acceptance: { level: "checked", evidence: ["changed-files", "commands-run", "validation-output"] }
3. MONITORAR com watchdog:
   loop:
     subagent_wait({ id: run_id, timeoutMs: 60000 })
     se completou → coletar resultado
     se needs_attention → steer/resume (máx 2 recuperações)
     se falha dupla → stop + re-delegar (retryCount++)
     retryCount >= 3 → STATUS: blocked, escalar para humano
4. REVIEW: delegar para reviewer (builtin, read-only):
   - Arquivos modificados + critérios de aceite da subtask
   - Se REJEITADO → re-delegar developer com issues (retryCount++)
5. QA: delegar para qa-tester-{nivel}:
   - Task: validar critérios de aceite + build + test
   - Se REJEITADO → re-delegar developer com feedback
6. COMMIT:
   git add .
   git commit -m "feat(ST-{nn}): {titulo da subtask}"
7. CONTEXT CLEANUP: próxima subtask inicia com ZERO contexto

### PARALELISMO

- Máximo 2 subagents simultâneos (concurrency: 2)
- Lotes paralelos: lançar ambos async, monitorar ambos no watchdog loop
- Só iniciar lote N+1 após TODAS as subtasks do lote N completarem
- Se uma subtask do lote falhar: bloquear dependentes, continuar independentes

### GATES

- Gate de Lote: todas as subtasks do lote APROVADAS (dev + review + QA)
- Gate de Fase: após última subtask → QA final valida integração
  - qa-tester-alto: dotnet build + dotnet test + validar critérios da spec
  - Se REJEITADO → identificar subtask responsável, re-abrir loop

### VALIDAÇÃO FINAL (após última subtask da fase)

Delegar para qa-tester-alto:
  1. dotnet build CATRA.sln -c Release
  2. dotnet test CATRA.sln
  3. Validar CADA critério de aceite da fase (seção "Critérios de Aceitação" da spec)
  4. Verificar regressões
  Retorne: RELATÓRIO FINAL com status por critério

### ENCERRAMENTO

1. Archiving: mover .agents/sprint_atual/ → .agents/Learning/sprints/SPRINT_{nn}/
2. Relatório de sprint via engineer
3. Git tag: v0.{FASE}.0

### TRATAMENTO DE ERROS

- "ESCALA_NECESSARIA" do developer → re-delegar para nível superior
  (baixo→medio→alto→critical). Se já é critical → escalar para humano.
- "NECESSITO_CONTEXTO" → fornecer contexto mínimo, re-delegar. Máx 3 tentativas.
- Build falha no QA → re-delegar developer com erro exato.
- retryCount >= 3 → STATUS: blocked, parar e reportar ao usuário.

### REGRAS INVIOLÁVEIS

1. NUNCA passar contexto de uma subtask para outra
2. NUNCA exceder 2 subagents em paralelo
3. NUNCA abandonar subagent sem coletar resultado
4. NUNCA marcar subtask como completa sem build-gate
5. NUNCA pular review ou QA
6. SEMPRE commit após cada subtask aprovada
7. SEMPRE usar fresh context para cada delegação
```

---

## USO RÁPIDO

### Executar Fase 1 completa:
```
🚀 execute the sprint CATRA — Fase 1
```

### Executar Fase 2 completa:
```
🚀 execute the sprint CATRA — Fase 2
```

### Executar subtask individual:
```
🚀 execute the sprint CATRA — ST-05 apenas
```

### Executar a partir de uma subtask específica:
```
🚀 execute the sprint CATRA — Fase 1, iniciar em ST-04
```

### Re-executar subtask que falhou:
```
🚀 execute the sprint CATRA — retry ST-08
```

---

## NOTAS

- O prompt referencia skills que já existem: orchestration, auto-delegate, build-gate, complexity-eval, caveman
- Agentes developer-{nivel} e qa-tester-{nivel} já estão configurados com a stack CATRA
- O reviewer é o builtin do pi (read-only)
- Cada subtask em .agents/specs/subtasks/ST-*.md é autocontida (escopo, critérios, dependências)
- O BACKLOG.md tem o grafo de dependências completo
- Build-gate usa `dotnet build CATRA.sln` (não mais RazeGas_Backend.slnx)

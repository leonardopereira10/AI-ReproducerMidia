# 🚀 CATRA — Script de Continuação (Fase 2)

> Cole o bloco PROMPT abaixo em uma **nova sessão de chat** (mesmo cwd `C:/Projetos/Reprodutor_CATRA`).
> Ele retoma a orquestração de onde parou: finaliza ST-20, executa ST-21/ST-22, QA final, tag e archiving.

---

## PROMPT (copiar daqui)

```
🚀 continue the sprint CATRA — Fase 2 (retomar de ST-20)

## CONTEXTO

Projeto: CATRA — Reprodutor de Mídia Desktop (WPF .NET 8+, C# 12)
CWD: C:/Projetos/Reprodutor_CATRA
Spec: .agents/specs/catra-media-player.md
Executor original: .agents/specs/subtasks/SPRINT_EXECUTOR.md
Log de estado: .agents/sprint_atual/SPRINT_LOG.md (LEIA PRIMEIRO — tem o status completo)
Subtasks: .agents/specs/subtasks/ST-*.md (cada uma é autocontida)
Build: dotnet build CATRA.sln | Test: dotnet test CATRA.sln | Native: powershell -File scripts/build-native.ps1

Stack: .NET 8+, C# 12, WPF, FFmpeg.AutoGen, Vortice.Windows, SQLite (sqlite-net-pcl),
CommunityToolkit.Mvvm, NAudio, xUnit, FluentAssertions.
Native: C++20, CMake, vcpkg, FSR 4 SDK, RIFE v4 (ONNX/DirectML), AMF SDK.

Carregar skills: orchestration, auto-delegate, build-gate, complexity-eval, caveman.

## ESTADO ATUAL (verificar com git log / git status / dotnet test)

- FASE 1: COMPLETA (tag v0.1.0, 11/11 subtasks, arquivada em .agents/Learning/sprints/SPRINT_01/)
- FASE 2 commitada: ST-12 (5886c0e), ST-13 (3959069), ST-14 (9adfd23), ST-15 (e79052b),
  ST-16 (baa56f2), ST-17 (89b8b1f), ST-18 (f8a110b), ST-19 (485a06d), fix dispose VMs (9be098a)
- Testes: 477 (Core 2 + UI 87 + Data 20 + Services 368), build 0 warnings/0 errors
- ST-20 (Playback/DLNA usar processado): estava EM ANDAMENTO — pode haver working tree não commitado
- PENDENTES: ST-20 (finalizar), ST-21 (Cleanup), ST-22 (Settings processamento),
  QA FINAL da Fase 2, tag v0.2.0, archiving

## ⚠️ RESTRIÇÕES AMBIENTAIS (não mudaram)

- SEM MSVC/VS2022/vcpkg → build nativo C++ NÃO verificável aqui (validação por INSPEÇÃO;
  scripts/build-native.ps1 documenta o build real em máquina com toolchain)
- FSR 4 SDK license-gated (GPUOpen) → não baixar
- Ambiente headless (sem GPU display/ffmpeg binário/TV) → abstrair tudo com interfaces + fakes;
  playback real/DLNA real/GPU real = validação MANUAL documentada
- NÃO baixar binários grandes (ffmpeg, ONNX, SDKs)

## INSTRUÇÕES DE RETOMADA

### PASSO 0 — Diagnóstico (obrigatório antes de tudo)
1. Leia .agents/sprint_atual/SPRINT_LOG.md
2. `git log --oneline | head -15` e `git status --short`
3. `dotnet build CATRA.sln` + `dotnet test CATRA.sln` (confirmar baseline verde e contagem)

### PASSO 1 — Finalizar ST-20 (.agents/specs/subtasks/ST-20-use-processed.md)
- Se `git status` mostrar mudanças não commitadas do ST-20 (MediaFileResolver.cs,
  IMediaFileResolver.cs, MediaFileResolverTests.cs, PlayerViewModel.cs, CastingService.cs,
  PlayerView.xaml, App.xaml.cs):
  a. Se build+testes VERDES → rodar chain [reviewer → qa-tester-medio] focado em ST-20 →
     se APROVADO, commit `feat(ST-20): Playback/DLNA usar processado`
  b. Se build quebrado ou testes falhando → re-delegar fix a developer-medio com os erros exatos
  c. Se mudanças ausentes/incompletas → re-delegar ST-20 completo a developer-medio
     (task = conteúdo de ST-20-use-processed.md + regras padrão abaixo)

### PASSO 2 — ST-21 Cleanup on close + startup (.agents/specs/subtasks/ST-21-cleanup.md)
- developer-baixo | async, acceptance "checked", turnBudget {maxTurns:40,graceTurns:5}, timeoutMs 800000
- Loop: dev → chain [reviewer → qa-tester-baixo] → commit `feat(ST-21): Cleanup on close + startup`

### PASSO 3 — ST-22 Settings processamento + perfis (.agents/specs/subtasks/ST-22-settings-processing.md)
- developer-baixo | mesmos parâmetros
- Loop: dev → chain [reviewer → qa-tester-baixo] → commit `feat(ST-22): Settings processamento + perfis`

### PASSO 4 — QA FINAL da Fase 2
- Delegar a qa-tester-alto: `dotnet build CATRA.sln -c Release` + `dotnet test -c Release` +
  smoke do app (~5s) + validar critérios da Fase 2 (RF-03 pipeline/janela, RN-07/RN-08/RN-09/RN-10,
  Telas 3/6). Critérios nativos/GPU reais (RIFE/FSR4/AMF/D3D11VA, encode H.265 real, leak GPU,
  TV DLNA) = MANUAL (listar como pendente de validação em máquina com GPU+SDKs).
- Se REJEITADO (regressão real) → identificar subtask e re-abrir loop.

### PASSO 5 — Encerramento
1. Git tag: `git tag v0.2.0`
2. Archiving: copiar .agents/sprint_atual/ → .agents/Learning/sprints/SPRINT_02/ + RESULTADO.md
   (resumo: subtasks, commits, testes, validação manual pendente, lições)
3. Relatório final ao usuário (Fase 1 + Fase 2: o que está pronto, o que exige validação manual)

## LOOP POR SUBTASK (regras padrão — iguais às usadas até aqui)

Para cada subtask:
1. LER a subtask ST-nn-*.md (autocontida)
2. DELEGAR dev-{nivel}: task = conteúdo da subtask + contexto técnico (o que já existe pronto)
   + regras (APENAS esta subtask; build-gate obrigatório; NÃO commit; ESCALA_NECESSARIA se bloquear;
   headless: abstrair com fakes; nativo: inspeção)
   Parâmetros: async:true, acceptance:"checked", control watchdog
   (needsAttentionAfterMs:120000, activeNoticeAfterMs:300000), turnBudget/timeoutMs conforme complexidade
3. MONITORAR: subagent_wait({id, timeoutMs:300000}) em loop; se needs_attention → steer;
   se travado 2x → stop + re-delegar (retryCount++); retryCount>=3 → STATUS blocked, reportar
4. SE TIMEOUT com trabalho pronto (padrão observado): verificar `dotnet build`+`dotnet test`
   direto; se verde, prosseguir para review+QA (não re-delegar do zero)
5. REVIEW+QA: chain [{agent:reviewer, task focado time-boxed}, {agent:qa-tester-{nivel}, task com {previous}}]
   (sequencial — nunca 2 builds paralelos no mesmo cwd)
6. Se REJEITADO → re-delegar dev com issues exatos → re-review focado
7. COMMIT: `git add -A && git commit -m "feat(ST-nn): {titulo}"`
8. CONTEXT CLEANUP: próxima subtask com zero contexto (fresh)

## REGRAS INVIOLÁVEIS

1. NUNCA passar contexto de uma subtask para outra (fresh context por delegação)
2. NUNCA exceder 2 subagents em paralelo (Fase 2 é sequencial na prática — arquivos compartilhados)
3. NUNCA abandonar subagent sem coletar resultado
4. NUNCA marcar subtask completa sem build-gate (dotnet build + test verdes)
5. NUNCA pular review ou QA
6. SEMPRE commit após cada subtask aprovada
7. Nativo: manter padrões GuardCabi/RAII/keyed-mutex/catra_release_texture/catra_free
8. Tratar erros: ESCALA_NECESSARIA → subir nível (baixo→medio→alto→critical→humano);
   NECESSITO_CONTEXTO → fornecer contexto mínimo (máx 3)
```

---

## NOTAS PARA A NOVA SESSÃO

- O log `.agents/sprint_atual/SPRINT_LOG.md` tem o mapa completo (commits, retries, padrões).
- Cada `ST-*.md` em `.agents/specs/subtasks/` é autocontido (escopo, critérios, dependências).
- Padrão de timeout: subtasks grandes estouram o turn budget com o trabalho PRONTO — verificar
  build+test e seguir, não re-delegar do zero.
- Rejeições comuns e já resolvidas no passado (não repetir): context menu BindingProxy (ST-04),
  USE_DML dead code + leak texturas (ST-13), ownership caller-owned catra_release_texture/catra_free (ST-17),
  dispose de VM na navegação (fix sistêmico).
- Validação MANUAL pendente (inerente a hardware, listar no relatório final): build nativo real
  (build-native.ps1 com VS2022+vcpkg+SDKs), playback D3D11VA real, encode H.265 real (AMF),
  RIFE/FSR4 em GPU RDNA4, DLNA com TV real, leak GPU em batch de 5 episódios.

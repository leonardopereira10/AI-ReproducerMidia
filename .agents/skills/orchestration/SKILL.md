---
name: orchestration
description: |
  Workflow completo de sprint: planejamento → validação PO → execução por subtask
  → review → QA → commit → archiving. Carregar para QUALQUER tarefa não-trivial
  (>2 arquivos ou impacto público). Define gates obrigatórios, ordem de execução,
  retry policy e validação final. NÃO carregar para fast-path (≤2 arquivos triviais).
  Keywords: sprint, executar, planejar, subtask, orquestrar, workflow, delegar,
  validar, review, QA final, archiving.
  Ator: sessão principal (orquestrador).
---

# 🧠 Orchestration — RazeGas

## Quando Usar

Sempre que a sessão principal receber uma tarefa que não seja fast-path.
Classifique a entrada via skill `auto-delegate` e siga o workflow abaixo.

---

## ⚠️ LIMITE DE PARALELISMO (OBRIGATÓRIO)

**Máximo de 2 subagents em paralelo. SEMPRE.**

- `concurrency` em PARALLEL mode: **nunca > 2**
- `parallel` blocks em chains: **nunca > 2 agentes simultâneos**
- Fanouts (PO validando múltiplas subtasks, Dev detalhando stories): **máx 2 por vez**
- Se houver N tarefas paralelas → executar em lotes de 2 (batch)

```text
# EXEMPLO: 5 subtasks para detalhar → 3 lotes
Lote 1: subtask_01 + subtask_02  (paralelo, concurrency=2)
Lote 2: subtask_03 + subtask_04  (paralelo, concurrency=2)
Lote 3: subtask_05               (single)
```

> Isso se aplica a TODA delegação: planejamento, execução, QA final, archiving.

---

## 🔍 WATCHDOG — Monitoramento de Subagents (OBRIGATÓRIO)

Após delegar QUALQUER subagent (single, parallel ou chain), a sessão principal
**DEVE monitorar ativamente** até a conclusão. O objetivo é detectar agentes
travados e recuperá-los **sem intervenção do usuário**.

### Protocolo

```
1. LANÇAR subagent com control:
   {
     control: {
       enabled: true,
       needsAttentionAfterMs: 120000,    // alerta se parado >2min
       activeNoticeAfterMs: 300000,      // alerta se ativo >5min sem progresso
       activeNoticeAfterTurns: 15,       // alerta se >15 turns sem concluir
       notifyOn: ["active_long_running", "needs_attention"]
     }
   }

2. LOOP DE MONITORAMENTO (a cada ~60 segundos):
   ┌─────────────────────────────────────────────────────┐
   │  subagent_wait({ timeoutMs: 60000 })                │
   │  │                                                  │
   │  ├─ Run finalizou? → coletar resultado, sair loop   │
   │  │                                                  │
   │  ├─ Run precisa de atenção?                         │
   │  │  ├─ Verificar status:                            │
   │  │  │  { action: "status", id: "...",               │
   │  │  │    view: "transcript", lines: 40 }            │
   │  │  │                                               │
   │  │  ├─ Agente PAUSADO/STUCK?                        │
   │  │  │  → steer com nudge:                           │
   │  │  │    { action: "steer", id: "...",              │
   │  │  │      message: "Continue. Resuma progresso." } │
   │  │  │                                               │
   │  │  ├─ Agente NÃO respondeu ao steer?               │
   │  │  │  → resume:                                    │
   │  │  │    { action: "resume", id: "...",             │
   │  │  │      message: "Retome a tarefa. Se bloqueado, │
   │  │  │      retorne ESCALA_NECESSARIA." }            │
   │  │  │                                               │
   │  │  └─ 2ª recuperação falhou?                       │
   │  │     → STOP + re-delegar (retryCount++)           │
   │  │                                                  │
   │  └─ Run ainda ativo sem alerta? → voltar ao loop    │
   └─────────────────────────────────────────────────────┘

3. NUNCA abandonar um subagent sem coletar resultado.
```

### Regras do Watchdog

| Regra | Detalhe |
|-------|---------|
| **Intervalo** | `subagent_wait({ timeoutMs: 60000 })` — verifica a cada ~1 min |
| **1º nudge** | `steer` com mensagem de continuação |
| **2º nudge** | `resume` com instrução explícita |
| **Falha dupla** | `stop` → re-delegar do zero (incrementa `retryCount`) |
| **Máx recuperações** | 2 por subtask. Após isso, `retryCount++` e re-delegação normal |
| **Nunca** | Deixar subagent rodando indefinidamente sem monitoramento |
| **Nunca** | Pedir ao usuário para "recomeçar o chat" — o orquestrador resolve |

### Exemplo de Monitoramento

```text
# Após lançar subagent:
run_id = subagent({ agent: "developer-medio", task: "...", async: true,
                    control: { enabled: true, needsAttentionAfterMs: 120000 } })

# Loop:
loop:
  result = subagent_wait({ id: run_id, timeoutMs: 60000 })
  if result.completed → break
  if result.needsAttention:
    status = subagent({ action: "status", id: run_id, view: "transcript", lines: 40 })
    if stuck:
      subagent({ action: "steer", id: run_id, message: "Continue a implementação." })
      # aguarda mais 60s, se ainda travado:
      subagent({ action: "resume", id: run_id, message: "Retome. Se impossível, retorne ESCALA_NECESSARIA." })
      # se ainda falhar:
      subagent({ action: "stop", id: run_id })
      retryCount++ → re-delegar
```

---

## WORKFLOW OBRIGATÓRIO

```
┌─────────────────────────────────────────────────────────┐
│                   FASE 1: PLANEJAMENTO                  │
│                                                         │
│  1.1  Criar plano de execução    → .agents/specs/       │
│  1.2  PO valida o plano          → subagent: product-owner │
│  1.3  PO refina em user stories  → .agents/sprint_atual/│
│  1.4  Dev/QA detalha subtasks    → .agents/sprint_atual/│
│  1.5  PO valida soluções e ordena → subagent: product-owner │
│                                                         │
│  ✅ Gate: PO aprovou plano + subtasks ordenadas          │
├─────────────────────────────────────────────────────────┤
│                   FASE 2: EXECUÇÃO                      │
│                                                         │
│  Para cada subtask (na ordem definida pelo PO):          │
│    2.1  Developer implementa     → subagent: developer-{nivel} │
│    2.2  Reviewer aprova          → subagent: reviewer (builtin) │
│    2.3  QA valida alterações     → subagent: qa-tester-{nivel} │
│    2.4  Git commit                                      │
│                                                         │
│  Após a ÚLTIMA subtask:                                 │
│    2.5  QA executa a aplicação e valida contra spec     │
│         + arquivo refinado da sprint                    │
│                                                         │
│  ✅ Gate: QA aprovou validação final                    │
├─────────────────────────────────────────────────────────┤
│                   FASE 3: ENCERRAMENTO                  │
│                                                         │
│  3.1  Archiving obrigatório      → .agents/Learning/    │
│  3.2  Relatório de sprint                               │
└─────────────────────────────────────────────────────────┘
```

---

## FASE 1: PLANEJAMENTO

### 1.1 — Criar Plano de Execução

A sessão principal cria o plano em `.agents/specs/{nome}_plan.md`:

```markdown
# Plano de Execução — {Nome}

## Objetivo
{O que será entregue}

## Escopo
- {item 1}
- {item 2}

## Fora de Escopo
- {exclusão 1}

## Critérios de Aceite
- [ ] {critério 1}
- [ ] {critério 2}

## Riscos
- {risco 1}
```

### 1.2 — PO Valida o Plano

Delegar para `product-owner` com o plano como contexto:

```
Valide este plano de execução. Verifique:
- Escopo está claro e completo?
- Critérios de aceite são mensuráveis?
- Há riscos não identificados?
Retorne: APROVADO ou REJEITADO + justificativa + sugestões.
```

Se REJEITADO → incorporar feedback, reescrever, re-submeter. Máx 3 iterações.

### 1.3 — PO Refina em User Stories

Delegar para `product-owner`:

```
Refine este plano aprovado em user stories.
Crie UM arquivo por story em .agents/sprint_atual/:
  - Formato: story_{nn}_{slug}.md
  - Conteúdo: título, descrição, critérios de aceite, tipo (dev|qa)
```

### 1.4 — Dev/QA Detalha Subtasks

Para cada story, delegar para o agente adequado ao **tipo**:
- Story tipo `dev` → `developer-{nivel}` detalha a subtask
- Story tipo `qa` → `qa-tester-{nivel}` detalha a subtask

O agente deve:
1. Ler a story
2. Avaliar complexidade (skill `complexity-eval`)
3. Escrever a subtask detalhada em `.agents/sprint_atual/subtask_{nn}_{slug}.md`

Formato da subtask:

```markdown
# Subtask {nn}: {Título}

**Story:** story_{nn}_{slug}.md
**Tipo:** dev | qa
**Complexidade:** baixa | media | alta | critical
**Agente:** developer-{nivel} | qa-tester-{nivel}

## Descrição
{O que implementar/testar}

## Arquivos Alvo (fileScope)
- {arquivo 1}
- {arquivo 2}

## Passos
1. {passo 1}
2. {passo 2}

## Critérios de Aceite
- [ ] {critério 1}

## Dependências
- {dependência ou "nenhuma"}
```

### 1.5 — PO Valida Soluções e Ordena

Delegar para `product-owner` com TODAS as subtasks:

```
Valide as subtasks propostas. Para cada uma verifique:
- A solução proposta atende a story?
- A complexidade está adequada?
- Há dependências entre subtasks?

Depois ORDENE as subtasks na sequência de execução.
Retorne: lista ordenada + ajustes necessários.
```

O PO deve escrever a ordem em `.agents/sprint_atual/EXECUTION_ORDER.md`:

```markdown
# Ordem de Execução — Sprint {N}

| Ordem | Subtask | Agente | Dependência |
|-------|---------|--------|-------------|
| 1 | subtask_01_x.md | developer-medio | nenhuma |
| 2 | subtask_02_y.md | developer-alto | subtask_01 |
| 3 | subtask_03_z.md | qa-tester-medio | subtask_02 |
```

**✅ Gate de Planejamento:** PO aprovou plano + subtasks detalhadas + ordem definida.

---

## FASE 2: EXECUÇÃO

### Loop por Subtask (na ordem do PO)

Para cada subtask em `EXECUTION_ORDER.md`:

#### 2.1 — Developer Implementa

Delegar para `developer-{nivel}` com **APENAS** o contexto da subtask atual:

```
## ESCOPO (único trabalho permitido)
{conteúdo da subtask}

## CONTEXTO TÉCNICO (read-only)
.NET 10, C#, ASP.NET Core, EF Core, xUnit, Moq.

## ARQUIVOS ALVO (fileScope)
{fileScope da subtask}

## REGRAS
1. Trabalhe APENAS com esta subtask
2. NÃO leia outras subtasks
3. Se faltar info: "NECESSITO_CONTEXTO: <descrição>"
4. Invoque skill build-gate antes de finalizar
```

**PROIBIDO passar:** outras subtasks, histórico da sprint, conversas de outros agentes.

Se retornar `"ESCALA_NECESSARIA"` → re-delegar para agente do nível seguinte.
Se retornar `"NECESSITO_CONTEXTO"` → fornecer contexto mínimo, re-delegar. Máx 3 tentativas.

#### 2.2 — Reviewer Aprova

Após o developer finalizar, delegar para `reviewer` (builtin):

```
Revise as alterações desta subtask:
- Subtask: {conteúdo da subtask}
- Arquivos modificados: {lista de arquivos}
- Critérios de aceite: {critérios}

Verifique: código correto, segue convenções, sem regressões, build passa.
Retorne: APROVADO ou REJEITADO + issues encontrados.
```

Se REJEITADO → re-delegar para o developer com os issues. Incrementar `retryCount`.
`retryCount >= 3` → marcar `STATUS: blocked`, escalar para humano.

#### 2.3 — QA Valida Alterações

Após reviewer aprovar, delegar para `qa-tester-{nivel}`:

```
Valide as alterações desta subtask:
- Subtask: {conteúdo da subtask}
- Arquivos modificados: {lista}
- Critérios de aceite: {critérios}

Execute build + testes. Verifique se os critérios de aceite foram atendidos.
Retorne: APROVADO ou REJEITADO + evidências.
```

Se REJEITADO → re-delegar para o developer com feedback do QA.

#### 2.4 — Git Commit

Após QA aprovar:
```bash
git add .
git commit -m "subtask: {titulo}"
```

Emita `"CONTEXT CLEANUP: {subtaskId}"`. Próxima subtask inicia com ZERO contexto.

---

### Validação Final (após a ÚLTIMA subtask)

#### 2.5 — QA Executa e Valida Tudo

Delegar para `qa-tester-alto` ou `qa-tester-critical` (conforme complexidade da sprint):

```
## VALIDAÇÃO FINAL DE SPRINT

Execute a aplicação e valide TODOS os pontos:

### Spec original
{conteúdo do plano em .agents/specs/}

### Sprint refinado
{conteúdo do EXECUTION_ORDER.md + stories}

### Instruções
1. Execute `dotnet build RazeGas_Backend.slnx -c Release`
2. Execute `dotnet test`
3. Execute a aplicação (`dotnet run --project Razegas.WebApi`)
4. Valide CADA critério de aceite da spec
5. Valide CADA critério de aceite das stories
6. Verifique regressões

Retorne: RELATÓRIO FINAL com status por critério (APROVADO/REJEITADO) + evidências.
```

**✅ Gate de Execução:** QA aprovou validação final.

Se REJEITADO → identificar subtask responsável, re-abrir, re-executar loop 2.1-2.4.

---

## FASE 3: ENCERRAMENTO

### 3.1 — Archiving (OBRIGATÓRIO)

```powershell
$sprintId = "05"
$dest = ".agents\Learning\sprints\SPRINT_$sprintId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Move-Item -Path ".agents\sprint_atual\*" -Destination $dest -Force
Write-Output "ARCHIVED: .agents\sprint_atual → $dest"
```

### 3.2 — Relatório

Delegar para `engineer` gerar relatório em `.agents/Learning/sprints/SPRINT_{nn}/`.

→ `.agents/instructions/archiving.md`

---

## ROTEAMENTO POR COMPLEXIDADE

| Complexidade | Dev Agent | QA Agent | Thinking |
|-------------|-----------|----------|----------|
| `baixa` | `developer-baixo` | `qa-tester-baixo` | minimal |
| `media` | `developer-medio` | `qa-tester-medio` | medium |
| `alta` | `developer-alto` | `qa-tester-alto` | high |
| `critical` | `developer-critical` | `qa-tester-critical` | max |

→ `.agents/instructions/complexity-mapping.md`

---

## FAST-PATH

Tarefa trivial (≤2 arquivos, sem interface pública, sem mudança arquitetural) → execute diretamente, sem este workflow.

→ `.agents/instructions/fast-path-rules.md`

---

## CROSS-REFERENCES

- **Sprint Resilience (Watchdog):** `.agents/instructions/sprint-resilience.md`

- **Auto-delegate:** `.agents/skills/auto-delegate/SKILL.md`
- **Build Gate:** `.agents/skills/build-gate/SKILL.md`
- **Complexity Eval:** `.agents/skills/complexity-eval/SKILL.md`
- **ACP:** `.agents/instructions/context-management.md`
- **Fast-path:** `.agents/instructions/fast-path-rules.md`
- **FileScope:** `.agents/instructions/fileScope-convention.md`
- **Resilience:** `.agents/instructions/sprint-resilience.md`
- **Complexity Mapping:** `.agents/instructions/complexity-mapping.md`
- **Bug Fix:** `.agents/instructions/bug-fix-flow.md`
- **Caveman:** `.agents/skills/caveman/SKILL.md`

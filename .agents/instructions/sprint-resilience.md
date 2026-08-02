# Sprint Resilience — Retry, Merge, Archiving

## 1. Retry Policy (max 3 attempts)

Each subtask has `retryCount` in ACP schema. Default `maxRetries = 3`.

### Mechanic
- On re-delegation (subtask failed validation or build) → **increment `retryCount`**
- If `retryCount >= maxRetries` → mark `STATUS: blocked` and **escalate to human**
- Never re-delegate beyond 3 attempts — indicates structural problem, not implementation

### When to re-delegate
- Build failed
- Scope validation failed
- QA rejected

### When to ESCALATE (do NOT re-delegate)
- `retryCount >= 3`
- Recurring bug without clear cause
- Unresolvable merge conflict

## 2. Merge Strategy (git cherry-pick)

### Rule
- Each subtask is implemented and tested in isolation
- On success → `git add .` + `git commit -m "subtask: <title>"` on main branch
- **Never** create temporary branches for individual subtasks
- If merge conflict → resolve manually and continue

## 3. Watchdog — Recovery de Agentes Travados

### Regra
A sessão principal **nunca** abandona um subagent sem coletar resultado.
Monitoramento ativo é obrigatório em toda delegação.

### Mecânica

| Etapa | Ação | Tool |
|-------|------|------|
| Lançar | `control: { enabled: true, needsAttentionAfterMs: 120000, activeNoticeAfterMs: 300000 }` | `subagent(...)` |
| Monitorar | Loop a cada ~60s até conclusão | `subagent_wait({ id, timeoutMs: 60000 })` |
| 1º nudge | Mensagem de continuação | `{ action: "steer", id, message: "Continue." }` |
| 2º nudge | Instrução explícita de retomada | `{ action: "resume", id, message: "Retome. Se impossível: ESCALA_NECESSARIA." }` |
| Falha dupla | Stop + re-delegação | `{ action: "stop", id }` → `retryCount++` → re-delegar |

### Limites
- Máx **2 recuperações** por subtask (steer + resume)
- Após 2 falhas → `stop`, `retryCount++`, re-delegar do zero
- `retryCount >= 3` → `STATUS: blocked`, escalar para humano
- **Nunca** pedir ao usuário para "recomeçar o chat" — o orquestrador resolve

### Diagnóstico
Para inspecionar um agente travado antes de intervir:
```
{ action: "status", id: "...", view: "transcript", lines: 40 }
```
Verificar: último tool call, erro repetido, loop infinito, timeout.

---

## 4. Mandatory Archiving

### Rule
**ALWAYS move before delete.** Never execute `Remove-Item .agents/sprint_atual/` without confirming the destination exists with a full copy.

### Flow
```
1. New-Item -Path ".agents\Learning\sprints\SPRINT_N" -ItemType Directory -Force
2. Move-Item -Path ".agents\sprint_atual\*" -Destination ".agents\Learning\sprints\SPRINT_N\" -Force
3. Remove-Item -Path ".agents\sprint_atual\*" -Recurse -Force
```

> ⚠️ **OBRIGATÓRIO:** Mover antes de deletar. Se algo falhar após o cleanup, todo o contexto é perdido irreversivelmente.

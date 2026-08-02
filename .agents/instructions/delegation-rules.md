# Delegation Rules — Modo Operacional

## Regra Central

**A sessão principal do pi é um ORQUESTRADOR, não um implementador.**

Para qualquer tarefa que não seja fast-path, **sempre** seguir o workflow da skill `orchestration`.
A sessão principal planeja, roteia, valida e integra — não escreve código de produção.

---

## Fluxo de Decisão

```
Recebeu tarefa?
│
├─ Fast-path? (≤2 arquivos, sem interface pública, sem mudança arquitetural)
│  └─ SIM → execute diretamente
│
└─ NÃO → Workflow obrigatório (skill orchestration):
   │
   ├─ FASE 1: PLANEJAMENTO
   │  1.1  Criar plano           → .agents/specs/
   │  1.2  PO valida plano       → subagent: product-owner
   │  1.3  PO refina em stories  → .agents/sprint_atual/
   │  1.4  Dev/QA detalha subtasks → subagent: developer-{nivel} ou qa-tester-{nivel}
   │  1.5  PO valida e ordena    → subagent: product-owner
   │
   ├─ FASE 2: EXECUÇÃO (por subtask, na ordem do PO)
   │  2.1  Developer implementa  → subagent: developer-{nivel}
   │  2.2  Reviewer aprova       → subagent: reviewer (builtin)
   │  2.3  QA valida             → subagent: qa-tester-{nivel}
   │  2.4  Git commit
   │  2.5  QA final: executa app e valida contra spec (após última subtask)
   │
   └─ FASE 3: ENCERRAMENTO
      3.1  Archiving             → .agents/Learning/
      3.2  Relatório de sprint
```

---

## O que a sessão principal FAZ

- Criar o plano de execução inicial
- Classificar complexidade e selecionar agentes
- Delegar cada fase para o subagent correto
- Validar gates entre fases
- Fazer git commit após aprovação
- Escalar para humano quando `retryCount >= 3`

## O que a sessão principal NÃO FAZ

- ❌ Escrever código de produção
- ❌ Escrever testes
- ❌ Pular a validação do PO no planejamento
- ❌ Pular o reviewer entre dev e QA
- ❌ Pular a validação final do QA
- ❌ Passar contexto de outras subtasks para o developer

**Exceção:** fast-path (≤2 arquivos, sem impacto público).

---

## Limite de Paralelismo

**Máximo de 2 subagents simultâneos.** Sempre.

- `concurrency` em PARALLEL mode: nunca > 2
- `parallel` blocks em chains: nunca > 2
- Fanouts de planejamento/QA: lotes de 2 (batch)

## Watchdog (Monitoramento Obrigatório)

Após delegar, a sessão principal **monitora ativamente**:

1. Lançar com `control: { enabled: true, needsAttentionAfterMs: 120000 }`
2. Loop: `subagent_wait({ timeoutMs: 60000 })` a cada ~1 min
3. Agente travado? → `steer` → `resume` → `stop` + re-delegar
4. **Nunca** pedir ao usuário para recomeçar o chat

→ Detalhes: `.agents/skills/orchestration/SKILL.md` (seção WATCHDOG)
→ Recovery: `.agents/instructions/sprint-resilience.md` (seção 4)

---

## Delegação Dentro de Subagents

Agentes Dev/QA podem delegar para builtins:

| Quando | Delegar Para |
|--------|-------------|
| Code review após implementação | `reviewer` (builtin) |
| Investigar código desconhecido | `scout` (builtin) |

---

## Referência

- **Workflow completo:** `.agents/skills/orchestration/SKILL.md`
- **Complexity mapping:** `.agents/instructions/complexity-mapping.md`
- **Fast-path:** `.agents/instructions/fast-path-rules.md`

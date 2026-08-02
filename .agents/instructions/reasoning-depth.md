# Reasoning Depth — Cognitive Load Control

## Níveis de Profundidade

O thinking level é definido no **frontmatter** de cada agente (`thinking:` field).
Cada agente é um perfil fixo — não há adaptação interna.

| Nível | Thinking | Dev Agent | QA Agent | Escopo |
|-------|----------|-----------|----------|--------|
| `baixo` | minimal | `developer-baixo` | `qa-tester-baixo` | CRUD, validações, DTOs, converters simples. Zero edge cases. |
| `medio` | medium | `developer-medio` | `qa-tester-medio` | Serviços genéricos, converters, validators, paginação. 2-3 edge cases. |
| `alto` | high | `developer-alto` | `qa-tester-alto` | E2E, auth, multi-layer, performance, EF Core. Todos edge cases. |
| `critical` | max | `developer-critical` | `qa-tester-critical` | Zero-defect, OWASP, chaos engineering, memory analysis. Sem limite. |

## Regras

1. O orquestrador (skill `orchestration`) seleciona o agente pelo campo `complexidade` da subtask.
2. O thinking level é fixo por agente — o pi-subagents aplica automaticamente via frontmatter.
3. Se um agente precisar de mais profundidade → `"ESCALA_NECESSARIA: <descrição>"` → orquestrador re-delega para o nível seguinte.

# Complexity Mapping — Roteamento de Agentes

## Tabela Única de Mapeamento

| Complexidade | Dev Agent | QA Agent | Thinking |
|-------------|-----------|----------|----------|
| `baixa` | `developer-baixo` | `qa-tester-baixo` | minimal |
| `media` | `developer-medio` | `qa-tester-medio` | medium |
| `alta` | `developer-alto` | `qa-tester-alto` | high |
| `critical` | `developer-critical` | `qa-tester-critical` | max |

## Regra

**Fonte de verdade:** Este arquivo. Todos os outros arquivos devem REFERENCIAR apenas.

## Escalada

Se um agente retornar `"ESCALA_NECESSARIA"` → re-delegar para o agente do nível seguinte:
- `developer-baixo` → `developer-medio` → `developer-alto` → `developer-critical`
- `qa-tester-baixo` → `qa-tester-medio` → `qa-tester-alto` → `qa-tester-critical`

→ Referência: `.agents/instructions/complexity-mapping.md`

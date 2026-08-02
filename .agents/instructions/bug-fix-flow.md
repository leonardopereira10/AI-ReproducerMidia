# Bug Fix Flow — 4 Steps

## Fluxo Padrão

1. **Simulate Error** → criar teste que reproduz o bug
2. **Create Subtasks** → JSON atômico em `.agents/sprint_atual/` (com risk assessment)
3. **Execute Fix** → implementar seguindo as subtasks
4. **Validate** → testes passando + build limpo + regressão

## Regra

Este fluxo é padrão para TODOS os agents dev/qa. Não reimplementar nos agents individuais.

→ Referência: `→ .agents/instructions/bug-fix-flow.md`

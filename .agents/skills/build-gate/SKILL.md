---
name: build-gate
description: |
  Gate obrigatório: executar dotnet build (+ dotnet test para QA) antes de
  marcar qualquer subtask como completed. Carregar ao finalizar implementação,
  ao revisar código, ou ao validar entrega. Se build falha, bloqueia conclusão.
  Keywords: build, test, completar, finalizar, validar, gate, concluir subtask.
  Ator: developer-*, qa-tester-* (todos os níveis).
---

# SKILL: Build Gate

## When to Invoke
Sempre antes de marcar qualquer subtask como `completed`.

## Rules

### Para todos os agents Dev e QA:
```bash
dotnet build CATRA.sln --no-restore
```

### Para QA agents (build + test):
```bash
dotnet build CATRA.sln --no-restore
dotnet test --no-build
```

### Para subtasks com native bridge (ST-12+):
```bash
powershell -File scripts/build-native.ps1
dotnet build CATRA.sln --no-restore
```

### Regra obrigatória:
Se o build falhar → NÃO marcar como `completed`. Retornar erro com `STATUS: failed` + output do build. **Sem exceção.**

### Quando passa

- Build **e** testes (se aplicável) passaram sem erros
- Zero regressões introduzidas
- Output mostra `BUILD: SUCCESS`

### Quando falha

- **Build falha:**
  1. Cole o erro EXATO (sem resumo)
  2. Identifique a raiz: código novo ou existente?
  3. Se código novo → corrija o código
  4. Se código existente → avalie se é regressão ou falha pré-existente
  5. Re-run `dotnet build CATRA.sln` após correção

- **Test falha:**
  1. Cole o output EXATO do test (nome do teste, mensagem, stack trace)
  2. Identifique: teste novo quebra ou teste existente quebrou?
  3. Se teste novo → corrija o teste
  4. Se teste existente → é regressão? Se sim, corrija O CÓDIGO, não o teste
  5. Re-run `dotnet test` após correção

## Output
```
BUILD: SUCCESS (X warnings, Y errors)
TESTS: PASSED (Z tests) ou SKIPPED (build-gate only)
```

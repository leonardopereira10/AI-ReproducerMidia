# Atomic Context Management Protocol (ACP)

## Princípio
**NUNCA** passe especificação completa ou histórico inteiro para subagentes. Use arquivos de estado em `.agents\sprint_atual\`.

---

## ACP Schema — Subtask State File

```json
{
  "sprintId": "5",
  "subtaskId": "sprint_5_001",
  "type": "implementation|testing|planning|bugfix",
  "titulo": "Implementar validação de cliente",
  "descricao": "Criar método de validação no ClienteService com testes",
  "passos": ["Passo 1", "Passo 2"],
  "esperado": "Método ValidateCliente() com validação completa",
  "criterioAceite": "Método funciona para dados válidos/inválidos, testes passam",
  "complexidade": "baixa|media|alta|critical",
  "status": "pending|in_progress|completed|failed|blocked",
  "stack": ".NET 10, C#, ASP.NET Core, EF Core, xUnit, Moq",
  "dependencias": [],
  "referenceFiles": ["RazeGas.Domain/Services/ClienteService.cs"],
  "fileScope": ["RazeGas.Domain/Services/ClienteService.cs"],
  "retryCount": 0,
  "maxRetries": 3
}
```

---

## Enums

### `type`
| Valor | Uso |
|-------|-----|
| `implementation` | Criar/modificar código |
| `testing` | Criar/executar testes |
| `planning` | Escopo, prioridades, backlog |
| `bugfix` | Corrigir bug |

### `complexidade`
| Valor | Dev | QA |
|-------|-----|----|
| `baixa` | `developer-baixo` | `qa-tester-baixo` |
| `media` | `developer-medio` | `qa-tester-medio` |
| `alta` | `developer-alto` | `qa-tester-alto` |
| `critical` | `developer-critical` | `qa-tester-critical` |

→ Referência: `.agents/instructions/complexity-mapping.md` (fonte de verdade)

### `status`
> **Regra:** Mesmo valor no `.md` e no `.json`. **Sem mapeamento, sem tradução.**

| Valor | Significado |
|-------|------------|
| `pending` | Aguardando execução |
| `in_progress` | Em execução |
| `completed` | Feita, critérios atendidos |
| `failed` | Não feita, razão documentada |
| `blocked` | Bloqueado, razão documentada |

---

## Regras Fundamentais

### 1. Persistência antes de delegar
- Salve contexto em `.agents\sprint_atual\sprint_<id>_<subtask_id>.json`
- Inclua: ID, descrição isolada, passos, critérios de aceite

### 2. Leitura atômica
- Subagente lê **apenas** seu arquivo
- NÃO leia arquivos de outras subtasks
- NÃO leia histórico completo da sprint

### 3. Cleanup por subtask (antes da próxima)
```json
{
  "subtaskId": "sprint_5_001",
  "status": "completed",
  "artefatos": ["arquivo1.cs", "arquivo2.cs"],
  "observacoes": "nota opcional",
  "timestamp": "2026-07-13T12:00:00Z"
}
```
- Limpe contexto do orchestrator
- Emita: `"CONTEXT CLEANUP: sprint_5_001"`
- Próxima subtask inicia com ZERO contexto

### 4. Arquivamento (fim da sprint — OBRIGATÓRIO)

> ⚠️ **NUNCA execute `Remove-Item` sem antes confirmar que o diretório de destino existe e contém cópia de tudo.**

```powershell
$sprintId = "05"
$dest = ".agents\Learning\sprints\SPRINT_$sprintId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Move-Item -Path ".agents\sprint_atual\*" -Destination $dest -Force
Write-Output "ARCHIVED: .agents\sprint_atual → $dest"
```

Emita: `"SPRINT ARCHIVED: SPRINT_$sprintId"`

### 5. Cleanup final (remover diretório vazio)
```powershell
Remove-Item -Path ".agents\sprint_atual" -Recurse -Force -ErrorAction SilentlyContinue
```

Emita: `"CONTEXT CLEANUP"`

---

## Protocolo de Contexto Faltante

Se subagente precisa de mais informação:
- Retorne: `{"status": "blocked", "reason": "NECESSITO_CONTEXTO", "missing": "descrição"}`
- Orchestrator encontra contexto mínimo, re-delega
- Máximo 3 tentativas → escale para `engineer`

---

## Validação Pós-Execução

Antes de prosseguir, valide:
1. **Escopo**: resultado refere-se APENAS à subtask delegada?
2. **Formato**: output corresponde ao schema esperado?
3. **Status**: valor válido (`completed` | `failed` | `blocked`)?
4. **Contaminação**: resultado NÃO menciona outras subtask IDs?
5. **Build Gate**: `dotnet build` passou?

Falha na validação → re-delegue com prompt reforçado OU escale.

---

## Referência

- **Complexity Mapping:** `.agents/instructions/complexity-mapping.md`

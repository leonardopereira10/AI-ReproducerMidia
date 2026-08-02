# Archiving — Arquivamento de Sprints

## Regra

**NUNCA** execute `Remove-Item` sem antes confirmar que o diretório de destino existe e contém cópia de tudo.

## Script de Arquivamento

```powershell
# 1. Determinar número da sprint a partir do sprintId no ACP
$sprintId = "05"  # extrair do sprintId ex: "sprint_5_001" → "05"

# 2. Criar diretório de arquivamento
$dest = ".agents\Learning\sprints\SPRINT_$sprintId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# 3. MOVER (NÃO deletar) todo o conteúdo
Move-Item -Path ".agents\sprint_atual\*" -Destination $dest -Force

# 4. Confirmar arquivamento
Write-Output "ARCHIVED: .agents\sprint_atual → $dest"
```

Emita: `"SPRINT ARCHIVED: SPRINT_$sprintId"`

## Cleanup Final

```powershell
Remove-Item -Path ".agents\sprint_atual" -Recurse -Force -ErrorAction SilentlyContinue
```

Emita: `"CONTEXT CLEANUP"`

---

→ Referência: `.agents/instructions/context-management.md` (Step 4-5)

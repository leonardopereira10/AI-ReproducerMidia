---
name: knowledge-separation
description: |
  Reorganizar estrutura .agents/ separando conhecimento genérico (~/.agents/)
  de específico do projeto (.agents/project/). Carregar ao criar novo projeto,
  reestruturar agentes, mover skills entre user/project scope, ou quando agentes
  contêm referências hardcoded a tecnologias. Keywords: separar, reorganizar,
  reutilizar, mover agente, novo projeto, onboarding.
  Ator: engineer, sessão principal.
source: auto-skill
extracted_at: '2026-07-09T01:52:11.717Z'
---

# Knowledge Separation Pattern — Multi-Agent Systems

## Quando Usar

Quando você tem um projeto com `.agents/agents/`, `.agents/skills/`, `.agents/instructions/` e quer:
- Reutilizar agentes/skills em múltiplos projetos
- Manter conhecimento específico do projeto isolado
- Facilitar onboarding em novos projetos

## Estrutura de Diretórios

```
~/.agents/                         ← Usuário (genérico, reutilizável)
├── agents/                       ← Papéis genéricos (dev, qa, po, orchestrator)
├── instructions/                 ← Regras gerais (ACP, caveman, delegation)
└── skills/                       ← Skills de tecnologia/framework
    ├── build-gate/
    ├── complexity-eval/
    ├── caveman/
    └── testing-patterns/

C:\Projetos\MeuProjeto\.agents\   ← Projeto (específico)
├── project/                      ← Stack, arquitetura, convenções
├── skills/                       ← Só skills específicas do projeto
├── specs/                        ← Especificações
├── sprint_atual/                 ← Sprints em andamento
└── Learning/                     ← Histórico
```

## Critérios de Separação

### Mover para `~/.agents/` (usuário)

| Tipo | Critério | Exemplo |
|------|----------|---------|
| **Agents** | Descrevem papéis, não tecnologias | `developer`, `qa-tester` |
| **Instructions** | Regras gerais de orquestração | `context-management.md`, `caveman-communication.md` |
| **Skills** | Tecnologias/frameworks reutilizáveis | `testing-patterns`, `build-gate` |
| **Skills** | Padrões genéricos de processo | `build-gate`, `complexity-eval` |

### Manter no projeto `.agents/`

| Tipo | Critério | Exemplo |
|------|----------|---------|
| **project/** | Stack, arquitetura, convenções específicas | `stack.md`, `architecture.md` |
| **skills/** | Padrões específicos do projeto | `razegas-testing-patterns` |
| **specs/** | Especificações do projeto | `sprint_01_backlog.md` |
| **sprint_atual/** | Estado atual do projeto | Subtasks em andamento |
| **Learning/** | Histórico do projeto | Relatórios de sprint |

## Procedimento de Separação

### Passo 1: Analisar Agents

1. Ler cada agent em `.agents/agents/`
2. Identificar referências a tecnologias específicas (ex: ".NET", "Angular", "EF Core")
3. Reescrever agents para descrever apenas **papéis**:
   - ❌ "Developer .NET/C# para tarefas de média complexidade"
   - ✅ "Developer para tarefas de média complexidade"
4. Substituir menções a skills específicas por "invoque a skill de padrões do projeto"

### Passo 2: Criar `.agents/project/`

Extrair conhecimento específico do projeto para arquivos em `.agents/project/`:

```markdown
# stack.md
- Runtime: .NET 10.0
- Framework: ASP.NET Core
- ORM: EF Core
- Test: xUnit, Moq

# architecture.md
- 8 projetos em camadas
- Herança: Entity → BaseObjectWithId
- Endpoints: AbstractController<TDto>

# conventions.md
- Naming: PascalCase para classes
- File structure: One component per file
```

### Passo 3: Atualizar Skills

Skills genéricas devem ler de `.agents/project/` quando precisarem de contexto específico:

```markdown
## Project-Specific Details

**Always read from `.agents/project/` for project-specific knowledge:**
- `stack.md` → tech stack, libraries
- `architecture.md` → layer structure, entities
- `conventions.md` → naming rules, file structure

> **Note:** `.agents/project/` is in the current project directory, NOT `~/.agents/`.
```

### Passo 4: Mover para `~/.agents/`

1. **Agents:** Copiar para `~/.agents/agents/`, remover do projeto
2. **Instructions:** Copiar para `~/.agents/instructions/`, remover do projeto
3. **Skills genéricas:** Copiar para `~/.agents/skills/`, remover do projeto
4. **Skills específicas:** Manter em `.agents/skills/` do projeto

### Passo 5: Validar

```bash
# Verificar se não há referências quebradas
grep -r "developer-specialist-baixo" .agents/
grep -r "Project.Base" .agents/agents/

# Build validation
dotnet build --no-restore
```

## Exemplo: Agent Genérico vs Específico

### ❌ Específico (antes)
```markdown
---
name: developer
description: .NET/C# Developer — tarefas de média complexidade
---

# AGENT: Developer — Medium Complexity

## SKILLS TO INVOKE
- **Project patterns skill** — architecture, inheritance chain
- **Build gate** — execute before marking subtask as `pronto`

## SCOPE
- Generic service methods (CRUD with business logic)
- Converter implementations (entity ↔ DTO)
```

### ✅ Genérico (depois)
```markdown
---
name: developer
description: Developer — medium-complexity tasks
---

# AGENT: Developer — Medium Complexity

## SKILLS TO INVOKE
- **Project patterns skill** — architecture, inheritance chain
- **Build gate** — execute before marking subtask as `pronto`

## SCOPE
- Generic service methods (CRUD with business logic)
- Converter implementations (entity ↔ DTO)
```

## Benefícios

- ✅ **Reutilização:** Agents/skills funcionam em qualquer projeto
- ✅ **Manutenção:** Atualizações em `~/.agents/` afetam todos os projetos
- ✅ **Clareza:** Separação clara entre genérico e específico
- ✅ **Onboarding:** Novo projeto só precisa criar `.agents/project/`

## Armadilhas

- ❌ Não misturar conhecimento genérico com específico no mesmo arquivo
- ❌ Não hardcodar paths absolutos (ex: `C:\Projetos\RazeGas\`)
- ❌ Não esquecer de atualizar referências cruzadas após mover arquivos
- ✅ Sempre validar com build/test após a separação

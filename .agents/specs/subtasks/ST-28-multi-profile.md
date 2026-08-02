# Subtask 28: Múltiplos Perfis de Usuário — STUB

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Fase 4)
Depende de ST-07 (WatchState).

## Objetivo
Suporte a múltiplos perfis de usuário com WatchState independente por perfil.
Uso pessoal atualmente, mas arquitetura preparada.

## Escopo (resumido — detalhar na Fase 4)
### Arquivos a Criar
- `src/CATRA.Core/Models/UserProfile.cs`
- `src/CATRA.Data/Entities/UserProfileEntity.cs`
- `src/CATRA.Services/ProfileService.cs`

### Arquivos a Modificar
- Schema SQLite: adicionar `UserProfile` table, FK em `WatchState`
- `src/CATRA.UI/Views/HomeView.xaml` — seletor de perfil

## Requisitos Principais
- Tabela `UserProfile`: Id, Name, AvatarPath, CreatedAt
- `WatchState` ganha FK `ProfileId` (cada perfil tem progresso próprio)
- Seletor de perfil na Home (dropdown ou avatar click)
- Trocar perfil → reload WatchState + badges
- Default: 1 perfil "Default" criado na primeira execução
- Sem autenticação (uso local)

## Critérios de Aceitação
- [ ] Criar/trocar perfil funciona
- [ ] Progresso independente por perfil
- [ ] Assistidos separados por perfil
- [ ] Migração de schema (WatchState existente → ProfileId = 1)

## Dependências
- ST-07 (WatchState)

## Notas
- Migração: `ALTER TABLE WatchState ADD COLUMN ProfileId INTEGER DEFAULT 1`
- Sem login/senha — só seleção de perfil
- Futuro: perfil pode ter preferências próprias (skip intro, tema)

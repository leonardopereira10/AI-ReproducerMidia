# Subtask 01: Criar ILibraryApiService + DTOs em Core

**Story:** story_01_api_biblioteca.md
**Tipo:** dev
**Complexidade:** baixa
**Agente:** developer-baixo

## Descrição

Criar a interface `ILibraryApiService` e os DTOs de resposta em `CATRA.Core`:
- `ILibraryApiService` com métodos: `GetCategories()`, `GetItemsByCategory(int)`, `GetEpisodesByItem(int)`, `Search(string)`, `GetContinueWatching()`
- DTOs: `CategoryDto`, `MediaItemDto`, `EpisodeDto`, `SearchResultDto`, `ContinueWatchingDto`
- DTOs são records simples em `CATRA.Core.Models`

## Arquivos Alvo
- `src/CATRA.Core/Interfaces/ILibraryApiService.cs` (novo)
- `src/CATRA.Core/Models/CategoryDto.cs` (novo)
- `src/CATRA.Core/Models/MediaItemDto.cs` (novo)
- `src/CATRA.Core/Models/EpisodeDto.cs` (novo)
- `src/CATRA.Core/Models/SearchResultDto.cs` (novo)
- `src/CATRA.Core/Models/ContinueWatchingDto.cs` (novo)

## Passos
1. Criar DTOs como records em `CATRA.Core.Models`
2. Criar `ILibraryApiService` em `CATRA.Core.Interfaces`
3. DTOs devem ser independentes de entidades de domínio (mapear de Episode, MediaItem, etc.)

## Critérios de Aceite
- [ ] Interface definida com 5 métodos
- [ ] DTOs como records com propriedades públicas
- [ ] Compila sem erros
- [ ] Core não referencia Services ou Data

## Dependências
- nenhuma

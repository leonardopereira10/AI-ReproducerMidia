# Subtask 02: Implementar LibraryApiService em Services

**Story:** story_01_api_biblioteca.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Implementar `LibraryApiService` em `CATRA.Services.Library` que realiza as consultas
aos repositórios existentes (`ICategoryRepository`, `IMediaItemRepository`, `IEpisodeRepository`,
`IWatchStateRepository`, `IProcessedFileRepository`) e retorna os DTOs.

## Arquivos Alvo
- `src/CATRA.Services/Library/LibraryApiService.cs` (novo)

## Passos
1. Injetar repositórios via constructor
2. `GetCategories()`: query `ICategoryRepository.GetAll()` + contar items por categoria
3. `GetItemsByCategory(categoryId)`: query `IMediaItemRepository` filtrado + cover/poster info
4. `GetEpisodesByItem(itemId)`: query `IEpisodeRepository` + `IProcessedFileRepository` para availableProfiles
5. `Search(query)`: busca por título em MediaItem + Episode (LIKE ou contains)
6. `GetContinueWatching()`: query `IWatchStateRepository` com ProgressPct > 0 e < 100, ordenado por UpdatedAt desc, limit 20

## Critérios de Aceite
- [ ] Todos os 5 métodos implementados
- [ ] Mapeamento entidade→DTO correto
- [ ] availableProfiles inclui "original" sempre + perfis processados existentes
- [ ] Busca case-insensitive
- [ ] Testes unitários para cada método

## Dependências
- subtask_01 (DTOs e interface)

# Subtask 04: Testes LibraryApiService

**Story:** story_01_api_biblioteca.md
**Tipo:** qa
**Complexidade:** media
**Agente:** qa-tester-medio

## Descrição

Testes unitários do `LibraryApiService` com Moq + FluentAssertions.

## Arquivos Alvo
- `tests/CATRA.Services.Tests/Library/LibraryApiServiceTests.cs` (novo)

## Passos
1. Testar `GetCategories()` retorna categorias com contagem
2. Testar `GetItemsByCategory()` com categoria válida e inválida
3. Testar `GetEpisodesByItem()` com availableProfiles (com e sem processados)
4. Testar `Search()` case-insensitive, vazio, sem resultados
5. Testar `GetContinueWatching()` ordenação e filtro

## Critérios de Aceite
- [ ] ≥10 testes cobrindo happy path + edge cases
- [ ] Mocks de todos os repositórios
- [ ] Build + testes passam

## Dependências
- subtask_02

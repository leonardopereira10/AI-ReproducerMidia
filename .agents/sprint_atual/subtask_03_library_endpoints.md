# Subtask 03: Adicionar endpoints REST no WebControlServer

**Story:** story_01_api_biblioteca.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Adicionar os endpoints REST da biblioteca no `WebControlServer.HandleRequestAsync`.
O switch atual atende 5 rotas; expandir para incluir `/api/library/*`.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/WebControlServer.cs` (modificar)

## Passos
1. Adicionar `ILibraryApiService` como dependência opcional no construtor
2. Expandir o switch em `HandleRequestAsync` com as novas rotas:
   - `/api/library/categories` → `HandleApiCategoriesAsync`
   - `/api/library/categories/{id}/items` → `HandleApiItemsAsync`
   - `/api/library/items/{id}/episodes` → `HandleApiEpisodesAsync`
   - `/api/library/search` → `HandleApiSearchAsync`
   - `/api/library/continue-watching` → `HandleApiContinueWatchingAsync`
3. Extrair path params (id, query string) do `HttpContext.Request`
4. Serializar resposta com `JsonSerializerOptions { PropertyNamingPolicy = CamelCase }`
5. Retornar 404 quando `ILibraryApiService` for null (graceful degradation)

## Critérios de Aceite
- [ ] 5 novos endpoints funcionais
- [ ] Path params extraídos corretamente
- [ ] JSON com camelCase
- [ ] 404 graceful quando service não injetado
- [ ] Endpoint de search aceita query string `?q=term`

## Dependências
- subtask_02 (LibraryApiService implementado)

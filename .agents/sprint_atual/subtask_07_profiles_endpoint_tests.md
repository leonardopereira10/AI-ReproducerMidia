# Subtask 07: Endpoint de perfis + testes StreamService

**Story:** story_02_stream_service.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Adicionar endpoint `/api/library/episodes/{id}/profiles` no WebControlServer e
testes unitários do StreamService.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/WebControlServer.cs` (modificar — adicionar endpoint)
- `tests/CATRA.Services.Tests/Streaming/StreamServiceTests.cs` (novo)

## Passos
1. Adicionar `IStreamService` como dependência opcional no WebControlServer
2. Rota `/api/library/episodes/{id}/profiles` → `HandleApiProfilesAsync`
3. Extrair episodeId do path, chamar `IStreamService.GetAvailableProfiles(episodeId)`
4. Retornar JSON com lista de perfis
5. Testes: ResolveEpisode (3 perfis), GetAvailableProfiles, RegisterForStreaming, Unregister, MIME mapping

## Critérios de Aceite
- [ ] Endpoint retorna perfis disponíveis
- [ ] ≥8 testes unitários
- [ ] Build + testes passam

## Dependências
- subtask_03 (WebControlServer expandido)
- subtask_06 (StreamService)

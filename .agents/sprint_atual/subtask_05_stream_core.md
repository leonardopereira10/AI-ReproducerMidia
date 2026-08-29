# Subtask 05: Criar IStreamService + DTOs em Core

**Story:** story_02_stream_service.md
**Tipo:** dev
**Complexidade:** baixa
**Agente:** developer-baixo

## Descrição

Criar `IStreamService`, `StreamResolution`, `ProfileInfo` em `CATRA.Core`.

## Arquivos Alvo
- `src/CATRA.Core/Interfaces/IStreamService.cs` (novo)
- `src/CATRA.Core/Models/StreamResolution.cs` (novo)
- `src/CATRA.Core/Models/ProfileInfo.cs` (novo)

## Passos
1. `IStreamService` com: `RegisterForStreaming(filePath, contentType)→string`, `ResolveEpisode(episodeId, profile)→StreamResolution?`, `GetAvailableProfiles(episodeId)→List<ProfileInfo>`, `Unregister(token)→void`
2. `StreamResolution` record: FilePath, ContentType, StreamUrl, Width, Height, Fps, FileSizeBytes, Profile
3. `ProfileInfo` record: Name, Label, Width, Height, Fps, FileSizeBytes, IsProcessed
4. Adicionar `GetActiveStreamingFiles()` → `IReadOnlySet<string>` para o ProcessedFileUsage (D7)

## Critérios de Aceite
- [ ] Interface + records compilam
- [ ] Core não referencia Services
- [ ] `GetActiveStreamingFiles` na interface para proteção de arquivos

## Dependências
- nenhuma

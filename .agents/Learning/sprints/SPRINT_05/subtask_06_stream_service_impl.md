# Subtask 06: Implementar StreamService em Services

**Story:** story_02_stream_service.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Implementar `StreamService` em `CATRA.Services` que resolve perfis, registra no
`MediaHttpServer` e mantém tracking de tokens ativos (para D7 — proteção de arquivos).

## Arquivos Alvo
- `src/CATRA.Services/Streaming/StreamService.cs` (novo)

## Passos
1. Injetar `IMediaHttpServer`, `IEpisodeRepository`, `IProcessedFileRepository`
2. `RegisterForStreaming`: mapear extensão→MIME (D8), delegar para `MediaHttpServer.RegisterFile`, retornar URL
3. `ResolveEpisode(episodeId, profile)`:
   - "original" → `episode.FilePath`
   - "local"/"dlna" → buscar `IProcessedFileRepository.GetByEpisodeAndProfile(episodeId, profile)`
   - Se não encontrado → retornar null
   - Registrar no MediaHttpServer e retornar StreamResolution
4. `GetAvailableProfiles(episodeId)`: sempre incluir "original" + perfis processados existentes
5. `Unregister(token)`: remover do MediaHttpServer + remover do tracking set
6. Tracking: `ConcurrentDictionary<string, string>` (token→filePath) para `GetActiveStreamingFiles()`
7. Mapeamento MIME: `.mp4`→`video/mp4`, `.mkv`→`video/x-matroska`, `.webm`→`video/webm`, fallback `application/octet-stream`

## Critérios de Aceite
- [ ] Resolve funciona para os 3 perfis
- [ ] MIME mapping correto
- [ ] Tracking de tokens ativos funcional
- [ ] `GetActiveStreamingFiles()` retorna filepaths em uso
- [ ] Testes unitários

## Dependências
- subtask_05 (interface)

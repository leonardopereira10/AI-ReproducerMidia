# Story 02 — Serviço de Streaming

**Tipo:** dev
**Complexidade:** Média
**Espec:** `.agents/specs/web_panel_streaming_home.md` — Seção 2

## Descrição

Criar `IStreamService` / `StreamService` que resolve o arquivo correto para um episódio
dado o perfil escolhido (original/local/dlna), registra no `MediaHttpServer` e retorna
a URL de streaming pública. Inclui mapeamento content-type e endpoint de perfis.

## Critérios de Aceite

- [ ] `IStreamService` definido em Core com `RegisterForStreaming`, `ResolveEpisode`, `GetAvailableProfiles`, `Unregister`
- [ ] `StreamResolution` e `ProfileInfo` como records em Core
- [ ] `StreamService` em Services injeta `IMediaHttpServer`, `IEpisodeRepository`, `IProcessedFileRepository`
- [ ] Perfil "original" → `episode.FilePath`; "local"/"dlna" → `ProcessedFile` correspondente
- [ ] Mapeamento extensão→MIME: `.mp4`→`video/mp4`, `.mkv`→`video/x-matroska`, fallback `application/octet-stream`
- [ ] `GET /api/library/episodes/{id}/profiles` retorna perfis disponíveis com info de resolução
- [ ] Testes unitários

## Dependências

- Story 01 (compartilha repositórios)

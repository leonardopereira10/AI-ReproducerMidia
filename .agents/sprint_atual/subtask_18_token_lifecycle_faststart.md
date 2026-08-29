# Subtask 18: Token Lifecycle + movflags faststart

**Story:** story_06_integracao_di.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Implementar token lifecycle (cleanup de tokens inativos) e adicionar `movflags +faststart`
ao AudioMuxer para MP4 processados.

## Arquivos Alvo
- `src/CATRA.Services/Streaming/StreamService.cs` (modificar — TTL cleanup)
- `src/CATRA.Services/Processing/AudioMuxer.cs` (modificar — movflags)

## Passos
1. **Token lifecycle** no StreamService:
   - `ConcurrentDictionary<string, StreamToken>` com `LastAccessedAt`
   - Timer periódico (60s) verifica tokens com >5min inatividade → unregister
   - `Unregister` chamado automaticamente on: episode change, mode change, session end
   - Manual `Unregister` exposto para callers
2. **movflags +faststart** no AudioMuxer:
   - Localizar onde FFmpeg muxing é configurado (provavelmente na linha ~93-94)
   - Adicionar `-movflags +faststart` aos argumentos do muxer para MP4
   - Apenas para output .mp4 (não afeta .mkv)

## Critérios de Aceite
- [ ] Tokens com >5min inatividade são limpos automaticamente
- [ ] Troca de episódio desregistra token anterior
- [ ] MP4 processados têm moov atom no início (faststart)
- [ ] MKV não afetado pelo movflags
- [ ] Build passa

## Dependências
- subtask_06 (StreamService)

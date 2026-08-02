# Subtask 24: Legendas MKV + .srt — STUB

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-10, Fase 3)
Depende de ST-06 (PlayerView para overlay de legendas).

## Objetivo
Suporte a legendas: faixas embutidas em MKV (seleção) e .srt externo.
Renderizar como overlay no playback, não queimar no encode.

## Escopo (resumido — detalhar na Fase 3)
### Arquivos a Criar
- `src/CATRA.Services/Playback/SubtitleDecoder.cs` — FFmpeg subtitle decode
- `src/CATRA.Services/Playback/SubtitleRenderer.cs` — overlay WPF
- `src/CATRA.Services/Playback/SrtParser.cs` — parser .srt externo
- `src/CATRA.UI/Controls/SubtitleOverlayControl.xaml` + `.cs`
- `src/CATRA.Core/Models/SubtitleTrack.cs`

## Requisitos Principais
- MKV: enumerar faixas de legenda via FFmpeg (`AVMEDIA_TYPE_SUBTITLE`)
- Seleção de faixa: dropdown no player (CC button)
- .srt externo: detectar `{mesmo_nome}.srt` na pasta do arquivo
- Render: TextBlock WPF sobre o vídeo (posição, tamanho, cor configuráveis)
- Timing: sync com Clock do PlaybackEngine
- No processamento: extrair faixas de legenda do MKV → salvar .srt separado
  (para incluir no output processado como faixa, ou sidecar)
- ASS/SSA: suporte básico (converter para texto simples)

## Critérios de Aceitação
- [ ] MKV com 2+ faixas de legenda: dropdown lista todas
- [ ] Trocar faixa em tempo real funciona
- [ ] .srt externo carregado automaticamente
- [ ] Legenda sincronizada com áudio (±100ms)
- [ ] Overlay não interfere nos controles do player
- [ ] Sem legenda → overlay invisível (sem artifact)

## Dependências
- ST-06 (PlayerView + PlaybackEngine)

## Notas
- FFmpeg subtitle decode: `avcodec_decode_subtitle2`
- Subtitles em MKV: geralmente ASS/SSA ou SRT (HDMV PGS é bitmap, mais complexo)
- Overlay WPF: `Canvas` com `TextBlock` posicionado via `Margin`
- Alternativa: renderizar legenda na textura (mais complexo, melhor para DLNA)
  - Para DLNA: legenda precisa estar no stream ou TV não exibe
  - Fase 3: avaliar se queima legenda no encode DLNA ou se TV suporta sidecar

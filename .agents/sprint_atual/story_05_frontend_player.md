# Story 05 — Frontend: Player com Streaming

**Tipo:** dev
**Complexidade:** Alta
**Espec:** `.agents/specs/web_panel_streaming_home.md` — Seção 5 (Tela B)

## Descrição

Construir o player HTML5 no frontend. O `<video>` recebe a stream URL do servidor e
reproduz via HTTP byte-range. Inclui: controles de transporte, seletor de perfil,
reporte de posição via `timeupdate`, DLNA cast a partir do player, fila de próximos,
auto-play next episode, e fallback HEVC.

## Critérios de Aceite

- [ ] `<video>` HTML5 recebe streamUrl e reproduz (play/pause/seek/volume)
- [ ] Seletor de perfil (Original/Local/DLNA) com info de resolução/fps
- [ ] Troca de perfil envia `switchProfile` e atualiza `video.src` mantendo posição (±2s)
- [ ] `timeupdate` envia `reportProgress` ao servidor (~1s cadence)
- [ ] Evento `ended` envia mensagem `ended` ao servidor
- [ ] Detecção de codec: `canPlayType()` para HEVC; fallback para original se não suportado
- [ ] Seção DLNA cast: descobrir dispositivos, `castTo`, `stopCast`
- [ ] Fila de próximos episódios com auto-play
- [ ] Controles: play/pause, seek bar, volume, skip intro, next/prev
- [ ] Nota: Safari/iOS — autoplay exige gesto do usuário (documentar)

## Dependências

- Story 02 (StreamService fornece streamUrl)
- Story 03 (WebSocket commands + relay)
- Story 04 (router hash-based para `#/play/{id}`)

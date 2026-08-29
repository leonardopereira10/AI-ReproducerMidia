# Subtask 14: Frontend — Player HTML5 + Streaming

**Story:** story_05_frontend_player.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alto

## Descrição

Implementar o player HTML5 na view player: `<video>` com stream URL, controles,
seletor de perfil, reporte de posição, fallback HEVC, DLNA cast, fila e auto-play.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/wwwroot/index.html` (modificar — player section)
- `src/CATRA.Services/WebControl/wwwroot/css/style.css` (modificar — player styles)
- `src/CATRA.Services/WebControl/wwwroot/js/app.js` (modificar — player logic)

## Passos
1. **HTML5 `<video>`**: element na view player, recebe `streamUrl` do state
2. **Controles**: play/pause (video.play()/pause()), seek bar (video.currentTime), volume (video.volume)
3. **`timeupdate` event**: a cada ~1s, enviar `{ type: "reportProgress", position: video.currentTime }`
4. **`ended` event**: enviar `{ type: "ended" }`
5. **Seletor de perfil**: radio buttons ou dropdown (Original/Local/DLNA) → envia `{ type: "switchProfile", profile: "..." }`
6. **Profile switch handler**: ao receber state com novo streamUrl, salvar currentTime, trocar video.src, seek para posição salva
7. **Fallback HEVC**: antes de dar play, testar `video.canPlayType('video/mp4; codecs="hev1.1.6.L93.B0"')` — se vazio, enviar `{ type: "switchProfile", profile: "original" }`
8. **DLNA Cast section**: button "Descobrir dispositivos" → envia browse → render lista → click device → envia `castTo`
9. **Queue**: render próximos da state.queue → click → envia `playEpisode`
10. **Auto-play**: ao receber `ended` de volta (ou state com próximo), auto-carregar próximo episódio
11. **Skip intro**: button existente → envia `skipIntro`

## Critérios de Aceite
- [ ] Vídeo toca via HTTP streaming (byte-range)
- [ ] Controles funcionam (play/pause/seek/volume)
- [ ] timeupdate reporta posição ao servidor
- [ ] Seletor de perfil troca stream mantendo posição (±2s)
- [ ] Fallback HEVC detecta e troca para original
- [ ] DLNA cast funciona (descobrir + cast + stop)
- [ ] Fila visível com auto-play
- [ ] Safari/iOS: autoplay funciona após primeiro gesto do usuário

## Dependências
- subtask_13 (router + view structure)
- subtask_11 (switchProfile backend)
- subtask_12 (castTo backend)

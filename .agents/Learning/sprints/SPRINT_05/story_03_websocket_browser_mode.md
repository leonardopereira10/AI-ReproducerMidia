# Story 03 — WebSocket Estendido + Browser Mode Control Loop

**Tipo:** dev
**Complexidade:** Alta
**Espec:** `.agents/specs/web_panel_streaming_home.md` — Seções 3 e 6 (D6)

## Descrição

Estender o protocolo WebSocket para suportar: navegação de biblioteca, playback no browser
com eleição de player client, relay de comandos, reporte de posição, troca de perfil,
e cast DLNA via WebSocket. O browser que exibe o `<video>` é o "player client"; outros
dispositivos são observers/remote controls.

## Critérios de Aceite

- [ ] `WebControlCommand` suporta novos tipos: browse, playEpisode, switchProfile, castTo, stopCast, reportProgress, ended, ready
- [ ] `WebControlState` inclui: mode (browser/dlna/idle), streamUrl, availableProfiles, availableDevices, isPlayerClient
- [ ] Player client election: primeiro `playEpisode` torna-se player client; um por vez
- [ ] Comando relay: servidor envia `{ "type": "command", "command": {...} }` para player client
- [ ] `reportProgress` do browser atualiza posição no servidor e persiste `WatchState`
- [ ] `ended` do browser dispara auto-play / cleanup
- [ ] `switchProfile` troca stream URL mantendo posição (tolerância ±2s)
- [ ] `castTo` / `stopCast` integram com `ICastingService`
- [ ] `WebSocketHandler` roteia mensagens por tipo de cliente (player vs observer)
- [ ] Testes unitários

## Dependências

- Story 01 (browse commands dependem da API de biblioteca)
- Story 02 (playEpisode depende do StreamService)

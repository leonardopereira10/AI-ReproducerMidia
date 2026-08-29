# Subtask 09: Player Client Management no WebControlService

**Story:** story_03_websocket_browser_mode.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alto

## Descrição

Implementar a lógica de player client election, tracking e command relay no
`WebControlService`. O player client é o browser que está exibindo o `<video>`.
Apenas um por vez. Comandos de outros clientes são relayed para o player client.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/WebControlService.cs` (modificar)
- `src/CATRA.Core/Interfaces/IWebControlService.cs` (modificar)

## Passos
1. Adicionar estado no WebControlService:
   - `string? _playerClientId` — ID do player client atual
   - `int _currentStreamEpisodeId` — episódio em streaming
   - `string? _currentStreamUrl` — URL do stream ativo
   - `string _activeProfile` — perfil ativo ("original"/"local"/"dlna")
   - `string _mode` — "browser"/"dlna"/"idle"
2. `IWebControlService` adicionar:
   - `void SetPlayerClient(string? clientId)` — define/remove player client
   - `string? PlayerClientId { get; }`
   - `event EventHandler<WebControlCommand>? CommandForPlayer` — relay
3. `HandleCommandAsync` estendido:
   - `playEpisode`: resolver via `IStreamService`, registrar player client, set mode="browser", emitir state com streamUrl
   - `switchProfile`: resolver novo perfil, manter posição (±2s), emitir novo streamUrl
   - `reportProgress`: atualizar posição, persistir WatchState
   - `ended`: disparar lógica de próximo episódio
   - `ready`: atualizar duração
   - `browse`: delegar para ILibraryApiService, emitir message tipo "library"
   - `castTo`/`stopCast`: delegar para ICastingService, set mode="dlna"
4. Relay: quando comando chega de observer, emitir `CommandForPlayer` para o WebSocketHandler rotear

## Critérios de Aceite
- [ ] Player client election: primeiro playEpisode vira player client
- [ ] Segundo playEpisode com player ativo → takeover (último vence)
- [ ] Desconexão do player client → limpa estado (mode=idle)
- [ ] reportProgress persiste WatchState via IWatchStateRepository
- [ ] ended dispara auto-play (prepara próximo episódio)
- [ ] Comandos de observer são relayed para player client
- [ ] Mode transições: idle→browser (playEpisode), idle→dlna (castTo), browser→dlna (castTo), dlna→browser (playEpisode)

## Dependências
- subtask_06 (StreamService)
- subtask_08 (models estendidos)

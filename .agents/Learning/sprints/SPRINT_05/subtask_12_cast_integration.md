# Subtask 12: Integração castTo/stopCast via WebSocket

**Story:** story_03_websocket_browser_mode.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Implementar comandos `castTo` e `stopCast` no `WebControlService.HandleCommandAsync`
para iniciar/encerrar casting DLNA a partir do web panel.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/WebControlService.cs` (modificar)

## Passos
1. Handler `castTo`:
   - Buscar dispositivo por UDN (descobrir se necessário via `ICastingService.DiscoverDevicesAsync`)
   - Resolver arquivo via `IStreamService.ResolveEpisode(episodeId, profile)`
   - Chamar `ICastingService.StartCastingAsync(device, filePath, title, duration)`
   - Set mode="dlna", limpar streamUrl
   - Se havia player client ativo → limpar (mode é mutuamente exclusivo — D4)
2. Handler `stopCast`:
   - Chamar `ICastingService.StopCastingAsync()`
   - Set mode="idle"
3. Adicionar `DiscoverDevices` command para popular `availableDevices` no state

## Critérios de Aceite
- [ ] castTo inicia DLNA casting com episódio/perfil correto
- [ ] stopCast encerra casting
- [ ] Transição browser→dlna limpa player client
- [ ] Transição dlna→browser (playEpisode) para casting
- [ ] DiscoverDevices retorna lista de dispositivos

## Dependências
- subtask_09 (player client management)
- subtask_06 (StreamService)

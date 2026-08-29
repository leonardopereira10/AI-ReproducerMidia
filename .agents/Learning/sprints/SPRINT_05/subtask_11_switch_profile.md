# Subtask 11: switchProfile — Troca de Stream Mantendo Posição

**Story:** story_03_websocket_browser_mode.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alto

## Descrição

Implementar a lógica de troca de perfil em tempo real. O usuário troca entre
Original/Local/DLNA sem perder a posição de reprodução (tolerância ±2s).

## Arquivos Alvo
- `src/CATRA.Services/WebControl/WebControlService.cs` (modificar — HandleCommandAsync case "switchProfile")

## Passos
1. Handler `switchProfile` no `HandleCommandAsync`:
   - Capturar posição atual (`_positionSec`)
   - Chamar `IStreamService.ResolveEpisode(currentEpisodeId, newProfile)`
   - Se null (perfil não existe) → ignorar ou emitir erro
   - Unregister token antigo
   - Register novo arquivo → novo streamUrl
   - Atualizar `_activeProfile`, `_currentStreamUrl`
   - Emitir state atualizado com novo streamUrl + posição atual
2. Player client recebe state → atualiza `video.src` e seek para posição (±2s)
3. Se perfil é igual ao atual → no-op

## Critérios de Aceite
- [ ] Troca de perfil emite novo streamUrl
- [ ] Posição mantida no state (player client faz seek)
- [ ] Perfil inexistente → sem crash, state não muda
- [ ] Mesmo perfil → no-op
- [ ] Token antigo desregistrado

## Dependências
- subtask_09 (player client management)
- subtask_06 (StreamService.ResolveEpisode)

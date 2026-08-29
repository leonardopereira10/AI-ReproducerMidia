# Subtask 08: Estender WebControlCommand + WebControlState

**Story:** story_03_websocket_browser_mode.md
**Tipo:** dev
**Complexidade:** baixa
**Agente:** developer-baixo

## Descrição

Estender os models `WebControlCommand` e `WebControlState` com os novos campos
necessários para browser mode.

## Arquivos Alvo
- `src/CATRA.Core/Models/WebControlCommand.cs` (modificar)
- `src/CATRA.Core/Models/WebControlState.cs` (modificar)

## Passos
1. `WebControlCommand`: adicionar campos opcionais:
   - `int? EpisodeId` (para playEpisode)
   - `string? Profile` (para playEpisode, switchProfile)
   - `string? Target` (para browse: "categories", "items", "episodes")
   - `int? CategoryId` (para browse items)
   - `int? ItemId` (para browse episodes)
   - `string? DeviceUdn` (para castTo)
2. `WebControlState`: adicionar campos:
   - `string Mode` ("browser" | "dlna" | "idle")
   - `string? StreamUrl`
   - `string? SeriesTitle`
   - `List<ProfileInfo>? AvailableProfiles`
   - `List<DlnaDeviceInfo>? AvailableDevices`
   - `bool IsPlayerClient`
3. Atualizar construtores e usages existentes (WebControlService, WebSocketHandler)

## Critérios de Aceite
- [ ] Novos campos opcionais (backward compat com JSON existente)
- [ ] WebControlState construtor atualizado
- [ ] Compila sem erros (ajustar callers existentes)

## Dependências
- subtask_05 (ProfileInfo)

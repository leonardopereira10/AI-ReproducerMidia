# Subtask 01: Interfaces e Modelos do Web Control (CATRA.Core)

**Story:** story_01_web_control_core.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição
Criar as interfaces e modelos no CATRA.Core que definem o contrato do painel web.

## Arquivos Alvo (fileScope)
- `src/CATRA.Core/Interfaces/IWebControlHub.cs` (NOVO)
- `src/CATRA.Core/Interfaces/IWebControlService.cs` (NOVO)
- `src/CATRA.Core/Models/WebControlState.cs` (NOVO)
- `src/CATRA.Core/Models/WebControlCommand.cs` (NOVO)

## Passos
1. Criar `WebControlState` — record com todas as propriedades do estado:
   - IsPlaying (bool), IsPaused (bool), Position (double), Duration (double)
   - Volume (int), Title (string), ThumbnailUrl (string?)
   - SkipIntroSec (double), CanSkipIntro (bool)
   - HasNextEpisode (bool), HasPreviousEpisode (bool)
   - CastDeviceName (string?), ProfileLabel (string?)
   - Queue (List<WebControlQueueItem>)
   
2. Criar `WebControlQueueItem` — record:
   - Id (int), Title (string), DurationSec (double), IsCurrent (bool)

3. Criar `WebControlCommand` — record para comandos recebidos via WebSocket:
   - Type (string) — "play", "pause", "seek", "volume", "skipIntro", "nextEpisode", "previousEpisode"
   - Position (double?) — para seek
   - Level (int?) — para volume
   - Método estático `FromJson(string json)` para parse

4. Criar `IWebControlHub`:
   ```csharp
   Task SendFullStateAsync(WebControlState state);
   Task BroadcastPositionAsync(double position, double duration);
   Task NotifyQueueChangedAsync(IReadOnlyList<WebControlQueueItem> queue);
   ```

5. Criar `IWebControlService`:
   ```csharp
   WebControlState GetCurrentState();
   Task HandleCommandAsync(WebControlCommand command);
   event EventHandler<WebControlState>? StateChanged;
   event EventHandler<(double Position, double Duration)>? PositionChanged;
   ```

## Critérios de Aceite
- [ ] `WebControlState` com todas as propriedades listadas
- [ ] `WebControlQueueItem` com Id, Title, DurationSec, IsCurrent
- [ ] `WebControlCommand` com Type, Position?, Level? e FromJson
- [ ] `IWebControlHub` com 3 métodos
- [ ] `IWebControlService` com GetCurrentState, HandleCommandAsync, eventos
- [ ] Projeto CATRA.Core compila sem erros

## Dependências
- nenhuma

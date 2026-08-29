# Subtask 03: WebControlService — Orquestração de Estado e Comandos

**Story:** story_03_web_control_service.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alto

## Descrição
Implementar `WebControlService` que orquestra ICastingService, ISlidingWindowService para manter estado consolidado e processar comandos.

## Arquivos Alvo (fileScope)
- `src/CATRA.Services/WebControl/WebControlService.cs` (NOVO)

## Passos
1. Criar `WebControlService` com dependências:
   - `ICastingService _casting`
   - `ISlidingWindowService _slidingWindow`
   - `IEpisodeRepository _episodes`
   - `IMediaItemRepository _mediaItems`
   - `IAppSettingsRepository _appSettings`
   - `IThumbnailService _thumbnails` (se existir, senão resolver path direto)

2. Estado interno:
   - `_currentState` — WebControlState atualizado por eventos
   - `_currentEpisodeId` — episódio em reprodução
   - `_currentEpisode` — Episode model
   - `_currentMediaItem` — MediaItem model

3. Subscrever eventos do ICastingService:
   - `StateChanged` → atualiza IsPlaying/IsPaused
   - `PositionChanged` → atualiza Position, dispara evento PositionChanged
   - `MediaEnded` → atualiza estado

4. Método `SetCurrentEpisode(int episodeId)`:
   - Carrega Episode + MediaItem do repositório
   - Atualiza Title, Duration, SkipIntroSec, ThumbnailUrl
   - Atualiza fila do ISlidingWindowService
   - Dispara StateChanged

5. `GetCurrentState()` → retorna snapshot de _currentState

6. `HandleCommandAsync(WebControlCommand command)`:
   - `play` → `_casting.PlayAsync()`
   - `pause` → `_casting.PauseAsync()`
   - `seek` → `_casting.SeekAsync(TimeSpan.FromSeconds(command.Position))`
   - `volume` → `_casting.SetVolumeAsync(command.Level)`
   - `skipIntro` → calcula target = currentPos + skipIntroSec, verifica CanSkipIntroAt, seek
   - `nextEpisode` → resolve próximo episódio, para casting, abre novo, retoma casting
   - `previousEpisode` → resolve anterior, mesmo fluxo

7. Lógica de fila:
   - Lê `_slidingWindow.WindowEpisodes` para popular Queue
   - Marca episódio atual como IsCurrent = true
   - Resolve thumbnail: `_thumbnails.GetThumbnailPathAsync(episodeId)` ou Episode.ThumbnailPath

8. Lógica de navegação de episódio (next/prev):
   - `_episodes.GetNextEpisode(currentId)` / `GetPreviousEpisode(currentId)`
   - Para retomar casting: capturar `_casting.CurrentDevice`, stop, open new, start casting

## Critérios de Aceite
- [ ] Subscreve eventos do ICastingService corretamente
- [ ] GetCurrentState() retorna snapshot completo e consistente
- [ ] Play/Pause/Seek/Volume funcionam via ICastingService
- [ ] SkipIntro usa mesma lógica do PlayerViewModel (CanSkipIntroAt + seek)
- [ ] Next/Previous navegam e retomas transmissão automaticamente
- [ ] Fila populada de ISlidingWindowService
- [ ] Thumbnail URL resolvida
- [ ] Projeto compila sem erros

## Dependências
- subtask_01 (interfaces e modelos)

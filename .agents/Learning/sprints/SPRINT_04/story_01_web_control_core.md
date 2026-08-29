# Story 01: Core — Interfaces e Modelo de Estado

## Descrição
Criar as interfaces e modelos no CATRA.Core que definem o contrato do painel web:
- `IWebControlHub` — publica eventos de estado para clientes conectados
- `IWebControlService` — expõe estado atual + comandos de controle
- `WebControlState` — modelo com todas as informações do estado atual

## Tipo
dev

## Critérios de Aceite
- [ ] `IWebControlHub` com métodos: SendStateAsync, BroadcastPositionAsync, NotifyQueueChangedAsync
- [ ] `IWebControlService` com: GetCurrentState(), PlayAsync(), PauseAsync(), SeekAsync(), SetVolumeAsync(), SkipIntroAsync(), NextEpisodeAsync(), PreviousEpisodeAsync()
- [ ] `WebControlState` com: IsPlaying, IsPaused, Position, Duration, Volume, Title, ThumbnailUrl, SkipIntroSec, CanSkipIntro, HasNextEpisode, HasPreviousEpisode, CastDeviceName, ProfileLabel, Queue[]
- [ ] `WebControlCommand` — modelo para comandos recebidos via WebSocket
- [ ] Projeto compila sem erros

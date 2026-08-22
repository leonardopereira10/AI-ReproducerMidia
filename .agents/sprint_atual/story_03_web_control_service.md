# Story 03: WebControlService — Orquestração de Estado e Comandos

## Descrição
Implementar `WebControlService` que orquestra ICastingService, ISlidingWindowService, IWatchStateService para:
- Manter estado atual consolidado (posição, fila, info do vídeo)
- Processar comandos recebidos (play, pause, seek, volume, skipIntro, next, previous)
- Subscrever eventos do CastingService para atualizar estado em tempo real
- Expor método para snapshot completo do estado

## Tipo
dev

## Critérios de Aceite
- [ ] Subscreve StateChanged, PositionChanged, MediaEnded do ICastingService
- [ ] Atualiza estado interno com posição (~1s), estado de playback, volume
- [ ] Processa comando Play → casting.PlayAsync()
- [ ] Processa comando Pause → casting.PauseAsync()
- [ ] Processa comando Seek → casting.SeekAsync(position)
- [ ] Processa comando Volume → casting.SetVolumeAsync(level)
- [ ] Processa comando SkipIntro → seek current + skipIntroSec (mesma lógica PlayerViewModel)
- [ ] Processa comando NextEpisode → navega e retoma transmissão
- [ ] Processa comando PreviousEpisode → navega e retoma transmissão
- [ ] Lê fila de ISlidingWindowService para populate queue
- [ ] Resolve thumbnail URL via IThumbnailService
- [ ] Projeto compila sem erros

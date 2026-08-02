# Subtask 19: UI Pre-Processar (Radio-Switch + Fila)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Telas 2 e 3, RF-03)
Depende de ST-18 (SlidingWindowService) e ST-04 (MediaDetailView).

## Objetivo
UI para iniciar/gerenciar pre-processamento: radio-switch de perfil na Detail,
tela de fila com progresso por etapa, badges de status nos cards de episódios.

## Escopo
### Arquivos a Criar
- `src/CATRA.UI/Views/ProcessingQueueView.xaml` + `.cs` — tela de fila (Tela 3)
- `src/CATRA.UI/ViewModels/ProcessingQueueViewModel.cs`
- `src/CATRA.UI/Controls/ProcessJobCardControl.xaml` + `.cs` — card de job na fila
- `src/CATRA.UI/Controls/ProfileRadioSwitch.xaml` + `.cs` — radio Local/DLNA
- `src/CATRA.UI/Converters/JobStatusToIconConverter.cs`
- `src/CATRA.UI/Converters/StepToLabelConverter.cs` — "Upscale (FSR4)"

### Arquivos a Modificar
- `src/CATRA.UI/Views/MediaDetailView.xaml` — habilitar seção Pre-Processar
- `src/CATRA.UI/ViewModels/MediaDetailViewModel.cs` — comandos de pre-processamento
- `src/CATRA.UI/Controls/EpisodeCardControl.xaml` — badges de status (⚙/✓/○)
- `src/CATRA.App/App.xaml.cs` — registrar ProcessingQueueView

### Arquivos NÃO tocar
- `src/CATRA.Services/Processing/` — não modificar (ST-17/18 prontos)

## Requisitos Técnicos

### MediaDetailView — Seção Pre-Processar (Tela 2)
- Radio-switch: `(●) Local  ( ) DLNA` — ProfileRadioSwitch control
- Label: "Janela: 5 episódios | ~16.5 GB estimados" (calculado)
- Botão "📥 Iniciar" → `SlidingWindowService.StartWindowAsync(mediaItemId, profile)`
- Botão vira "⏹ Parar" se janela ativa para esta série
- Estimativa: `5 × duration_avg × bitrate / 8` (aproximação)
- Se janela ativa para outra série → warning: "Trocar de série cancela a fila atual"

### EpisodeCardControl — Badges
- `⚙ fila` — episódio na janela (queued ou processing)
- `✓ pronto` — ProcessedFile existe para o perfil ativo
- `○` — sem processamento (original)
- `↻` — stale (hash mudou, precisa reprocessar)
- Badge cor: ⚙ amarelo, ✓ verde, ○ cinza, ↻ laranja
- Bind para `ProcessedFile` + `ProcessJob` via ViewModel

### ProcessingQueueView (Tela 3)
- Header: "📥 Pre-Processamento — {Series Title}"
- Perfil ativo: "Local (1080p 135fps)" ou "DLNA (4K 55fps)"
- Disco usado: "{used} / {estimated} GB" com barra
- Seção "Processando agora":
  - ProcessJobCardControl com:
    - "EP{NN} — {DisplayTitle}"
    - ProgressBar com % + etapa atual: "58% — Upscale (FSR4)"
    - ETA: "~3 min"
    - Botão "✕ Cancelar"
- Seção "Na janela":
  - Lista de 5 episódios com status:
    - `✓ EP01  3.1 GB  (pronto)`
    - `⚙ EP03  ...     (processando)`
    - `○ EP05  ~3.3 GB (na fila)`
- Footer: "Ao assistir EP01 → deleta EP01, enfileira EP06"
- Acesso: botão na Detail ou item na toolbar da Home

### ProcessingQueueViewModel
- Bind para `IProcessingQueueService` events
- `CurrentJob` com progresso em tempo real (ProgressChanged event)
- `WindowEpisodes` com status (pronto/processando/fila)
- `CancelCommand` → `QueueService.CancelCurrentAsync()`
- `DiskUsed`: somar `ProcessedFile.FileSizeBytes` da série ativa
- `DiskEstimated`: `windowSize × avgDuration × bitrate / 8`
- Timer de refresh: 500ms para progresso suave

### ProfileRadioSwitch
- Custom control: dois RadioButtons estilizados como toggle
- `SelectedProfile` dependency property (ProcessProfile enum)
- Visual: pill/toggle switch com "Local" e "DLNA"
- Disabled durante processamento ativo

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Radio-switch seleciona Local/DLNA
- [ ] Botão "Iniciar" enfileira 5 episódios (visível na fila)
- [ ] Fila mostra progresso em tempo real (step + %)
- [ ] ETA exibido e razoável
- [ ] Cancelar job funciona
- [ ] Badges nos episódios atualizam (⚙ → ✓ ao completar)
- [ ] Disco usado atualiza conforme processa
- [ ] Trocar de série → warning + cancela fila anterior
- [ ] Botão "Parar" limpa fila da série
- [ ] ProcessingQueueView acessível da Detail

## Dependências
- ST-18 (SlidingWindowService + ProcessingQueueService)
- ST-04 (MediaDetailView + EpisodeCardControl)

## Notas
- Progress binding: usar `INotifyPropertyChanged` no ViewModel, atualizar via
  `Dispatcher.Invoke` (events vêm de background thread)
- Estimativa de disco: grosseira mas útil (bitrate × duração)
- Badges: consultar ProcessedFile + ProcessJob por episódio (query leve)
- Animação de progresso: `DoubleAnimation` no ProgressBar (suave)

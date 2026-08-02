# Subtask 06: Player Controls Customizados

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-05, Tela 4, RN-04)
Depende de ST-05 (PlaybackEngine funcional).

## Objetivo
UI do player com controles customizados embutidos no app: play/pause, seek bar,
volume, tempo, pular abertura, transmitir, fullscreen. Integrado ao
PlaybackEngine da ST-05.

## Escopo
### Arquivos a Criar
- `src/CATRA.UI/Views/PlayerView.xaml` + `.cs` — tela do player
- `src/CATRA.UI/ViewModels/PlayerViewModel.cs`
- `src/CATRA.UI/Controls/SeekBarControl.xaml` + `.cs` — seek bar custom
- `src/CATRA.UI/Controls/VolumeControl.xaml` + `.cs` — slider de volume
- `src/CATRA.UI/Controls/TransportControls.xaml` + `.cs` — barra de controles
- `src/CATRA.UI/Converters/TimeSpanToStringConverter.cs` — "12:34 / 22:00"
- `src/CATRA.UI/Converters/PlaybackStateToIconConverter.cs` — ▶/⏸

### Arquivos a Modificar
- `src/CATRA.UI/Views/MediaDetailView.xaml` — clique em episódio abre PlayerView
- `src/CATRA.App/App.xaml.cs` — registrar PlayerView no navigation

### Arquivos NÃO tocar
- `src/CATRA.Services/Playback/PlaybackEngine.cs` — não modificar (ST-05)
- `src/CATRA.Data/` — sem DB

## Requisitos Técnicos

### PlayerView (Tela 4)
- Layout: vídeo ocupa área principal, controles na parte inferior
- Controles auto-hide após 3s de inatividade (mouse sem mover)
- Controles reaparecem ao mover mouse ou clicar
- Fundo preto letterbox (manter aspect ratio do vídeo)
- `VideoHostControl` (ST-05) embedado no centro

### TransportControls
- Botões: Play/Pause (toggle), Stop
- SeekBar: slider com thumb arrastável, buffer indicator (futuro)
  - Click → seek para posição
  - Drag → preview de posição (tooltip com timestamp)
  - Release → seek efetivo
- Tempo: "12:34 / 22:00" (atual / total)
- Volume: slider 0-100% + ícone mute toggle
- Botão "⏭ Pular Abertura" — sempre visível (RN-04)
- Botão "📺 Transmitir" — placeholder (ST-08 implementa dropdown)
- Botão "⛶ Tela Cheia" — toggle fullscreen
- Indicador de perfil: "🖥 1080p135" ou "📺 4K55" (placeholder por agora)

### Pular Abertura (RN-04)
- Default: +85 segundos (1:25)
- Valor vem de `MediaItem.SkipIntroSec` (override por série)
- Ao clicar: `PlaybackEngine.Seek(current + skipIntroSec)`
- Desabilitar se `current + skip > duration - 30`
- Tooltip mostra o valor: "Pular +1:25"

### Fullscreen
- F11 ou botão → toggle fullscreen
- Fullscreen: WindowStyle=None, WindowState=Maximized, topmost
- ESC sai do fullscreen
- Controles continuam funcionando em fullscreen
- Vídeo preenche tela mantendo aspect ratio

### PlayerViewModel
- Recebe `IPlaybackEngine` via DI
- `OpenAsync(episode)` → carrega arquivo, extrai metadata, inicia
- Bindable: Position, Duration, Volume, State, IsFullscreen
- `PlayCommand`, `PauseCommand`, `StopCommand`, `SeekCommand`, `SkipIntroCommand`
- Timer de UI: atualiza Position a cada 250ms (do PlaybackEngine.PositionChanged)
- Ao fechar player → Stop + dispose
- Dialog "Continuar de MM:SS?" se WatchState tem progresso < 85% (RN-03)
  - "Continuar" → seek para LastPositionSec
  - "Do início" → seek para 0

### SeekBar Custom
- `Slider` WPF customizado com template
- Thumb: círculo com glow
- Track: barra fina com progresso preenchido
- Hover: expande altura (3px → 8px)
- Drag: thumb segue mouse, tooltip com timestamp acima
- Sem "jump" ao clicar (seek suave)

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Player abre ao clicar em episódio na Detail
- [ ] Play/Pause/Stop funcionam
- [ ] SeekBar: click e drag funcionam com seek preciso
- [ ] Volume slider controla áudio
- [ ] Tempo exibe "MM:SS / MM:SS" atualizado
- [ ] Pular Abertura avança 1:25 (ou valor da série)
- [ ] Pular Abertura desabilita perto do final
- [ ] Fullscreen (F11 + botão) funciona, ESC sai
- [ ] Controles auto-hide após 3s
- [ ] Dialog "Continuar de MM:SS?" aparece quando aplicável
- [ ] Aspect ratio mantido (letterbox)

## Dependências
- ST-05 (PlaybackEngine + VideoRenderer)
- ST-04 (MediaDetailView — clique em episódio)

## Notas
- Botão "Transmitir" é placeholder até ST-08
- Indicador de perfil é placeholder até ST-20
- Seleção de áudio/legenda é placeholder até Fase 3
- Auto-hide dos controles: DispatcherTimer + MouseMove event
- Considerar `System.Windows.Threading.DispatcherTimer` para UI timer

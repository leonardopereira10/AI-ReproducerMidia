# Subtask 09: Thumbnails (FFmpeg Frame Capture)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-08, RN-05)
Depende de ST-02 (Episode.ThumbnailPath) e ST-05 (FFmpeg libs disponíveis).

## Objetivo
Serviço que extrai thumbnail de cada vídeo (frame em ~10% da duração),
salva em cache local, e disponibiliza para os cards da UI. Fallback para
placeholder genérico.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Metadata/ThumbnailService.cs` — extração + cache
- `src/CATRA.Core/Interfaces/IThumbnailService.cs`
- `tests/CATRA.Services.Tests/Metadata/ThumbnailServiceTests.cs`

### Arquivos a Modificar
- `src/CATRA.UI/Controls/MediaCardControl.xaml` — bind para thumbnail real
- `src/CATRA.UI/Controls/EpisodeCardControl.xaml` — bind para thumbnail real
- `src/CATRA.UI/ViewModels/HomeViewModel.cs` — carregar thumbnails async
- `src/CATRA.UI/ViewModels/MediaDetailViewModel.cs` — carregar thumbnails async
- `src/CATRA.App/App.xaml.cs` — registrar IThumbnailService

### Arquivos NÃO tocar
- `src/CATRA.Services/Playback/` — não modificar
- `src/CATRA.Data/` — não modificar schema

## Requisitos Técnicos

### ThumbnailService
```csharp
public interface IThumbnailService
{
    // Extrai thumbnail se não existe em cache
    Task<string?> GetOrCreateThumbnailAsync(Episode episode);

    // Extrai thumbnail para capa de série (usa primeiro episódio)
    Task<string?> GetOrCreateSeriesCoverAsync(MediaItem mediaItem);

    // Override manual
    Task SetCustomCoverAsync(int mediaItemId, string imagePath);

    // Limpar cache
    Task ClearCacheAsync();
}
```

### Extração (RN-05)
- Via `ffprobe`/`ffmpeg` CLI (Process) — mais simples que FFmpeg.AutoGen para frame único
- Comando:
  ```
  ffmpeg -ss {timestamp} -i {input} -frames:v 1 -q:v 2 -vf "scale=480:-1" {output.jpg}
  ```
- Timestamp: **10% da duração** (evita logo/abertura/copyright)
  - Se duração < 5min → 30% da duração
  - Se duração desconhecida → 30 segundos fixo
- Output: JPEG, largura 480px (proporcional), qualidade 2
- Cache: `%AppData%/CATRA/thumbs/{episodeId}.jpg`
- Capa de série: thumbnail do primeiro episódio, salvo como `{mediaItemId}_cover.jpg`

### Cache e Performance
- Verificar existência antes de extrair (não re-extrair)
- Extração em background (Task.Run, não bloqueia UI)
- Batch: ao abrir Home, extrair thumbnails visíveis primeiro (lazy)
- Se extração falha → retornar null → UI usa placeholder
- Timeout: 10s por thumbnail (arquivos grandes podem demorar no seek)

### Custom Cover (RF-08)
- `SetCustomCoverAsync`: copiar imagem para cache como `{mediaItemId}_cover_custom.jpg`
- Prioridade: custom > auto > placeholder
- Atualizar `MediaItem.CoverPath` no DB

### Integração UI
- `MediaCardControl`: `Image.Source` bind para thumbnail path
  - Se null → placeholder genérico (ícone de filme/série)
- `EpisodeCardControl`: idem
- Carregamento async: `BitmapImage` com `BeginInit/EndInit` em background
- Placeholder: ResourceDictionary com ícone vetorial

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa
- [ ] Thumbnail extraída em ~10% da duração
- [ ] Cache funciona (segunda chamada não re-extrai)
- [ ] Cards da Home exibem thumbnails reais
- [ ] Cards de episódios exibem thumbnails reais
- [ ] Placeholder aparece quando extração falha
- [ ] Custom cover via context menu funciona
- [ ] Capa de série usa thumbnail do primeiro episódio
- [ ] Extração não bloqueia UI

## Dependências
- ST-02 (Episode model com ThumbnailPath)
- ST-05 (FFmpeg libs em lib/ffmpeg/)

## Notas
- ffmpeg CLI deve estar em `lib/ffmpeg/bin/ffmpeg.exe`
- Alternativa: FFmpeg.AutoGen para frame capture (mais complexo, evita Process)
- Para MKV: ffmpeg lida nativamente
- Para AVI: pode precisar de codec — ffmpeg bundled deve cobrir
- Scale 480px: suficiente para cards, não ocupa muito disco (~30KB por thumb)

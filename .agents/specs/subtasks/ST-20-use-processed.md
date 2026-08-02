# Subtask 20: Playback/DLNA Usar Processado

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-05, RF-06, RN-08)
Depende de ST-17 (pipeline gera processados), ST-06 (player), ST-08 (DLNA).

## Objetivo
Fazer o player e o DLNA usarem o arquivo processado quando disponível,
com fallback para o original. Indicador de perfil no player. Mapeamento
de timestamp original ↔ processado.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Playback/MediaFileResolver.cs` — resolve qual arquivo usar
- `src/CATRA.Core/Interfaces/IMediaFileResolver.cs`
- `tests/CATRA.Services.Tests/Playback/MediaFileResolverTests.cs`

### Arquivos a Modificar
- `src/CATRA.UI/ViewModels/PlayerViewModel.cs` — usar resolver, indicador de perfil
- `src/CATRA.Services/Casting/CastingService.cs` — usar resolver para DLNA
- `src/CATRA.UI/Views/PlayerView.xaml` — indicador de perfil visível

### Arquivos NÃO tocar
- `src/CATRA.Services/Playback/PlaybackEngine.cs` — não modificar
- `src/CATRA.Services/Processing/` — não modificar

## Requisitos Técnicos

### IMediaFileResolver
```csharp
public interface IMediaFileResolver
{
    // Resolve qual arquivo reproduzir/transmitir
    Task<ResolvedMedia> ResolveAsync(int episodeId, ProcessProfile profile);
}

public record ResolvedMedia(
    string FilePath,           // caminho do arquivo (processado ou original)
    bool IsProcessed,          // true se usa processado
    ProcessProfile? Profile,   // perfil do processado (null se original)
    string DisplayLabel);      // "🖥 1080p135" ou "📺 4K55" ou "📄 Original"
```

### MediaFileResolver
```
ResolveAsync(episodeId, profile):
  processed = ProcessedFileRepo.GetByEpisodeAndProfile(episodeId, profile)
  IF processed != null AND File.Exists(processed.FilePath):
    // Verificar stale (RN-09)
    episode = EpisodeRepo.GetById(episodeId)
    IF processed.SourceHash != episode.FileHash:
      log: "Processed file stale for episode {id}"
      RETURN original (fallback)
    RETURN ResolvedMedia(processed.FilePath, true, profile, label)
  ELSE:
    episode = EpisodeRepo.GetById(episodeId)
    RETURN ResolvedMedia(episode.FilePath, false, null, "📄 Original")
```

### PlayerViewModel — Integração
- Ao abrir episódio:
  ```
  resolved = await MediaFileResolver.ResolveAsync(episodeId, ProcessProfile.Local)
  await PlaybackEngine.OpenAsync(resolved.FilePath)
  ProfileLabel = resolved.DisplayLabel
  ```
- Indicador no player: "🖥 1080p135" (processado local) ou "📄 Original"
- Se processado não existe → dialog:
  - "Arquivo processado não disponível. Reproduzir original?"
  - [Reproduzir Original] [Cancelar]
  - (Não oferece processar aqui — isso é na Detail via ST-19)

### CastingService — Integração
- Ao iniciar casting:
  ```
  resolved = await MediaFileResolver.ResolveAsync(episodeId, ProcessProfile.Dlna)
  await StartCastingAsync(device, resolved.FilePath, title)
  ```
- Se processado dlna não existe → dialog:
  - "Arquivo processado (4K 55fps) não disponível. Transmitir original?"
  - [Transmitir Original] [Cancelar]
- Status bar: "Transmitindo para {device} | {resolved.DisplayLabel}"

### Timestamp Mapping (RN-08)
- Duração é idêntica (interpolação não muda tempo total)
- `WatchState.LastPositionSec` sempre no original
- Se reproduzindo processado: posição mapeia 1:1
- Ao salvar progresso: salvar posição do playback (que é igual à do original)
- Não precisa de conversão — mas validar que durações batem (±1s tolerance)

### Indicador de Perfil (UI)
- Label no canto inferior direito do player: "🖥 1080p135" ou "📺 4K55"
- Cor: verde se processado, cinza se original
- Tooltip: "Arquivo processado (RIFE + FSR 4)" ou "Arquivo original"

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa (resolver com mock repo)
- [ ] Player usa processado local quando disponível
- [ ] Player usa original quando processado não existe
- [ ] DLNA usa processado dlna quando disponível
- [ ] DLNA usa original quando processado não existe
- [ ] Indicador de perfil visível no player
- [ ] Dialog de fallback aparece quando aplicável
- [ ] Stale detection: processado com hash diferente → fallback
- [ ] Timestamp mapeia corretamente (progresso portável)
- [ ] Status bar do DLNA mostra perfil

## Dependências
- ST-17 (pipeline gera ProcessedFile)
- ST-06 (PlayerViewModel)
- ST-08 (CastingService)
- ST-02 (ProcessedFileRepository)

## Notas
- Subtask de integração — pouca lógica nova, muita conexão
- Testar com e sem processado disponível
- Stale detection: hash check é barato (já está no DB)
- Futuro (ST-26): reprocessamento automático quando stale

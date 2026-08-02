# Subtask 07: Watch State + Threshold + Continuar

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-07, RN-02, RN-03, RN-08)
Depende de ST-02 (WatchState repo) e ST-06 (PlayerViewModel integra).

## Objetivo
Serviço que gerencia estado de reprodução: salvar progresso periodicamente,
calcular threshold de assistido, marcar automaticamente, expor "continuar
assistindo". Integrar ao PlayerViewModel e MediaDetailViewModel.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Playback/WatchStateService.cs` — lógica de negócio
- `src/CATRA.Core/Interfaces/IWatchStateService.cs`
- `tests/CATRA.Services.Tests/Playback/WatchStateServiceTests.cs`

### Arquivos a Modificar
- `src/CATRA.UI/ViewModels/PlayerViewModel.cs` — salvar progresso a cada 5s, marcar assistido ao atingir threshold
- `src/CATRA.UI/ViewModels/MediaDetailViewModel.cs` — separar assistidos/não-assistidos, context menu toggle
- `src/CATRA.UI/ViewModels/HomeViewModel.cs` — badge "Continuar Assistindo"
- `src/CATRA.App/App.xaml.cs` — registrar IWatchStateService

### Arquivos NÃO tocar
- `src/CATRA.Data/` — repos já prontos
- `src/CATRA.Services/Playback/PlaybackEngine.cs` — não modificar

## Requisitos Técnicos

### WatchStateService
```csharp
public interface IWatchStateService
{
    // Salvar progresso (chamado a cada 5s pelo player)
    Task SaveProgressAsync(int episodeId, double positionSec, double durationSec);

    // Verificar e marcar assistido se threshold atingido
    Task CheckAndMarkWatchedAsync(int episodeId, double positionSec, double durationSec);

    // Toggle manual
    Task ToggleWatchedAsync(int episodeId);
    Task MarkWatchedAsync(int episodeId, bool watched);

    // Consultas
    Task<WatchState?> GetStateAsync(int episodeId);
    Task<List<Episode>> GetContinueWatchingAsync();  // RN-03
    Task<bool> ShouldOfferContinueAsync(int episodeId);  // progress < 85% && pos > 30s
}
```

### Threshold de Assistido (RN-02)
```
CalcularThreshold(durationSec):
  SE durationSec < 180  → return 0.95
  SE durationSec < 300  → return 0.90
  SENÃO → return (durationSec - 90 - 90) / durationSec
  // 90s abertura + 90s encerramento
```
- Ao salvar progresso: `progressPct = positionSec / durationSec`
- Se `progressPct >= threshold` → `watched = true`
- Threshold calculado por episódio (duração varia)

### Continuar Assistindo (RN-03)
- `GetContinueWatchingAsync()`: episódios WHERE `progress > 0 AND watched = 0 AND progressPct < 0.85 AND lastPositionSec > 30`
- Ordenar por `WatchState.UpdatedAt DESC` (mais recente primeiro)
- HomeViewModel: badge no card da série se algum episódio qualifica
- PlayerViewModel: ao abrir episódio, se `ShouldOfferContinue` → dialog

### Timestamp no Original (RN-08)
- `LastPositionSec` sempre baseado no arquivo **original**
- Se reproduzindo processado: duração é igual → posição mapeia 1:1
- Se processado não existe e reproduz original: mesma lógica
- Portável entre reprocessamentos

### Integração PlayerViewModel
- Timer de 5s: `SaveProgressAsync(episodeId, position, duration)`
- Ao atingir threshold: auto-marcar + notificar UI (badge ✓)
- Ao fechar player (Stop): salvar progresso final
- Ao abrir: verificar `ShouldOfferContinue` → dialog "Continuar de MM:SS?"

### Integração MediaDetailViewModel
- Separar episódios em duas listas: Unwatched / Watched
- Context menu "Marcar assistido" → `MarkWatchedAsync(id, true)`
- Context menu "Desmarcar" → `MarkWatchedAsync(id, false)`
- Refresh listas após toggle

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa (testes de threshold com durações variadas)
- [ ] Progresso salvo a cada 5s durante playback
- [ ] Episódio 22min marca assistido em ~86.4% (19:00)
- [ ] Episódio 3min marca assistido em 95%
- [ ] Episódio 2min marca assistido em 95%
- [ ] Toggle manual funciona (context menu)
- [ ] "Continuar Assistindo" aparece na Home quando aplicável
- [ ] Dialog "Continuar de MM:SS?" aparece ao abrir episódio com progresso
- [ ] "Do início" reseta posição para 0
- [ ] Progresso persiste entre sessões (fechar e reabrir app)

## Dependências
- ST-02 (WatchStateRepository)
- ST-06 (PlayerViewModel para integração)

## Notas
- Testes de threshold: 22min→86.4%, 5min→90%, 2min→95%, 2h→97.5%
- Timer de 5s: usar `DispatcherTimer` ou `PeriodicTimer` (.NET 6+)
- Não bloquear UI ao salvar progresso (async fire-and-forget com error handling)

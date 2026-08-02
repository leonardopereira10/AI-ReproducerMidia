# Subtask 18: Janela Deslizante (Queue + Rotação)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-03, RN-10, RF-04)
Depende de ST-17 (pipeline) e ST-07 (watch state).

## Objetivo
Serviço que gerencia a janela deslizante de 5 episódios pré-processados por
série. Auto-rotação ao assistir, enfileiramento do próximo, integração com
ProcessJob/ProcessedFile no DB.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Processing/SlidingWindowService.cs` — lógica da janela
- `src/CATRA.Services/Processing/ProcessingQueueService.cs` — fila + worker
- `src/CATRA.Core/Interfaces/ISlidingWindowService.cs`
- `src/CATRA.Core/Interfaces/IProcessingQueueService.cs`
- `tests/CATRA.Services.Tests/Processing/SlidingWindowServiceTests.cs`

### Arquivos a Modificar
- `src/CATRA.Services/Playback/WatchStateService.cs` — hook: ao marcar assistido → notificar janela
- `src/CATRA.App/App.xaml.cs` — registrar serviços + iniciar worker

### Arquivos NÃO tocar
- `src/CATRA.Services/Processing/ProcessingPipeline.cs` — não modificar (ST-17)
- `src/CATRA.UI/` — sem UI (ST-19)

## Requisitos Técnicos

### IProcessingQueueService
```csharp
public interface IProcessingQueueService
{
    // Enfileirar episódios para processamento
    Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile);

    // Cancelar job ativo
    Task CancelCurrentAsync();

    // Limpar fila
    Task ClearQueueAsync();

    // Estado
    ProcessJob? CurrentJob { get; }
    List<ProcessJob> QueuedJobs { get; }
    event EventHandler<ProcessJob> JobStarted;
    event EventHandler<ProcessJob> JobCompleted;
    event EventHandler<ProcessJob> JobFailed;
    event EventHandler<PipelineProgress> ProgressChanged;
}
```

### ProcessingQueueService (Worker)
- Background worker: `Task` dedicado rodando loop
- `Channel<ProcessJob>` (System.Threading.Channels) para fila thread-safe
- Loop:
  ```
  WHILE running:
    job = await channel.ReadAsync(cancellationToken)
    job.Status = Processing; save to DB
    JobStarted?.Invoke(job)
    TRY:
      result = await pipeline.ProcessAsync(episode, config, progress, ct)
      IF result.Success:
        save ProcessedFile to DB
        job.Status = Completed
      ELSE:
        job.Status = Failed; job.ErrorMessage = result.Error
    CATCH OperationCanceledException:
      job.Status = Cancelled
    CATCH Exception ex:
      job.Status = Failed; job.ErrorMessage = ex.Message
    save job to DB
    JobCompleted/JobFailed?.Invoke(job)
  ```
- Persistir jobs em `ProcessJob` table (status, progresso, erro)
- Ao iniciar app: jobs com status `processing` → marcar como `failed` (crash recovery)

### ISlidingWindowService
```csharp
public interface ISlidingWindowService
{
    // Iniciar janela para uma série
    Task StartWindowAsync(int mediaItemId, ProcessProfile profile);

    // Parar janela (limpar fila da série)
    Task StopWindowAsync(int mediaItemId);

    // Notificar que episódio foi assistido (trigger rotação)
    Task OnEpisodeWatchedAsync(int episodeId);

    // Estado
    int? ActiveMediaItemId { get; }
    ProcessProfile? ActiveProfile { get; }
    List<Episode> WindowEpisodes { get; }  // episódios na janela
}
```

### SlidingWindowService (RN-10)
```
StartWindowAsync(mediaItemId, profile):
  // Se já tem janela ativa para OUTRA série → parar anterior
  IF ActiveMediaItemId != null AND ActiveMediaItemId != mediaItemId:
    await StopWindowAsync(ActiveMediaItemId)

  unwatched = EpisodeRepo.GetUnwatchedByMediaItem(mediaItemId)
  window = unwatched.Take(WINDOW_SIZE)  // default 5
  await QueueService.EnqueueAsync(window.Select(e => e.Id), profile)
  ActiveMediaItemId = mediaItemId
  ActiveProfile = profile
  WindowEpisodes = window

OnEpisodeWatchedAsync(episodeId):
  episode = EpisodeRepo.GetById(episodeId)
  IF episode.MediaItemId != ActiveMediaItemId: RETURN  // outra série

  // 1. Deletar processado do episódio assistido
  processed = ProcessedFileRepo.GetByEpisodeAndProfile(episodeId, ActiveProfile)
  IF processed != null:
    IF not in use (playing/streaming):
      File.Delete(processed.FilePath)
      ProcessedFileRepo.Delete(processed.Id)
      log: "Deleted processed: {path}"

  // 2. Enfileirar próximo
  unwatched = EpisodeRepo.GetUnwatchedByMediaItem(ActiveMediaItemId)
  currentWindowIds = WindowEpisodes.Select(e => e.Id)
  next = unwatched.FirstOrDefault(e => !currentWindowIds.Contains(e.Id))
  IF next != null:
    await QueueService.EnqueueAsync([next.Id], ActiveProfile)
    WindowEpisodes.Add(next)
    WindowEpisodes.Remove(episode)  // remover assistido da janela

StopWindowAsync(mediaItemId):
  await QueueService.ClearQueueAsync()  // só jobs desta série
  ActiveMediaItemId = null
  WindowEpisodes.Clear()
```

### Integração WatchStateService
- Ao `MarkWatchedAsync(episodeId, true)` → `SlidingWindowService.OnEpisodeWatchedAsync(episodeId)`
- Hook via evento ou injeção direta
- Não bloquear: fire-and-forget com error handling

### Config
- `WINDOW_SIZE`: de `AppSettings["window_size"]` (default 5)
- `PipelineConfig`: montado de AppSettings (fps, res, bitrate por perfil)

### Uma Série por Vez
- `StartWindowAsync` para série A, depois série B → para A automaticamente
- Jobs da série A em fila → cancelados
- Job ativo da série A → cancelado (CancellationToken)
- Log: "Switched window: {A} → {B}"

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa (testes de rotação com mock pipeline)
- [ ] StartWindow enfileira 5 primeiros não-assistidos
- [ ] Ao assistir EP01 → deleta processado + enfileira EP06
- [ ] Janela mantém sempre 5 (ou menos se não tem episódios suficientes)
- [ ] Trocar de série → cancela jobs anteriores
- [ ] Cancelar job ativo funciona
- [ ] Jobs persistem no DB (status visível após restart)
- [ ] Crash recovery: jobs "processing" → "failed" na inicialização
- [ ] Progress events propagam para UI (via eventos)
- [ ] Não deleta arquivo em uso (playing/streaming)

## Dependências
- ST-17 (ProcessingPipeline)
- ST-07 (WatchStateService — hook de assistido)
- ST-02 (ProcessJob, ProcessedFile repositories)

## Notas
- `System.Threading.Channels` é ideal para producer/consumer pattern
- CancellationTokenSource por job (cancelamento individual)
- Testes: mock IProcessingPipeline para não depender de GPU
- File.Delete pode falhar se arquivo em uso → retry 3x com delay 1s
- WINDOW_SIZE configurável mas default 5 (spec)

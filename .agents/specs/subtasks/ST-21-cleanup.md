# Subtask 21: Cleanup on Close + Startup

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-04, RN-10)
Depende de ST-18 (SlidingWindowService + ProcessedFile records).

## Objetivo
Serviço de cleanup que deleta todos os arquivos processados ao fechar o app,
e limpa órfãos na inicialização (crash recovery). Log de cleanup.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Storage/CleanupService.cs`
- `src/CATRA.Core/Interfaces/ICleanupService.cs`
- `tests/CATRA.Services.Tests/Storage/CleanupServiceTests.cs`

### Arquivos a Modificar
- `src/CATRA.App/App.xaml.cs` — hook OnExit → cleanup; OnStartup → cleanup residual
- `src/CATRA.App/App.xaml.cs` — registrar ICleanupService

### Arquivos NÃO tocar
- `src/CATRA.Services/Processing/` — não modificar
- `src/CATRA.UI/` — sem UI

## Requisitos Técnicos

### ICleanupService
```csharp
public interface ICleanupService
{
    // Deleta todos os processados (chamado no shutdown)
    Task<CleanupResult> CleanupAllAsync();

    // Deleta órfãos de crash anterior (chamado no startup)
    Task<CleanupResult> CleanupOrphansAsync();

    // Deleta processados de um episódio específico
    Task DeleteProcessedAsync(int episodeId);

    // Info
    long GetProcessedFolderSizeBytes();
}

public record CleanupResult(int FilesDeleted, long BytesFreed, List<string> Errors);
```

### CleanupAllAsync (Shutdown)
```
1. Cancelar jobs ativos (via ProcessingQueueService.CancelCurrentAsync)
2. Aguardar cancelamento (timeout 5s)
3. Query ProcessedFile table → todos os registros
4. Para cada registro:
   - Verificar se arquivo não está em uso (playing/streaming)
   - File.Delete(filePath)
   - Se falhar: retry 3x com delay 500ms
   - Se ainda falhar: log error, adicionar a Errors
5. Limpar tabelas: ProcessedFile (DELETE ALL), ProcessJob (DELETE ALL)
6. Log: "Cleanup: {N} arquivos deletados, {X} GB liberados"
7. Return CleanupResult
```

### CleanupOrphansAsync (Startup)
```
1. Verificar se processed_folder existe
2. Listar todos os arquivos em processed_folder (*.mp4)
3. Query ProcessedFile table
4. Órfãos = arquivos no disco SEM registro no DB
   (crash durante processamento → arquivo parcial sem registro)
5. Também: registros no DB sem arquivo no disco (crash após delete parcial)
6. Deletar órfãos do disco
7. Limpar registros stale do DB
8. Log: "Startup cleanup: {N} órfãos deletados"
```

### Integração App.xaml.cs
```csharp
// Startup
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    // Cleanup residual (async, não bloqueia startup)
    _ = Task.Run(async () =>
    {
        var result = await cleanupService.CleanupOrphansAsync();
        if (result.FilesDeleted > 0)
            log.Info($"Startup cleanup: {result.FilesDeleted} orphans deleted");
    });
}

// Shutdown
protected override void OnExit(ExitEventArgs e)
{
    // Cleanup síncrono com timeout (app está fechando)
    var task = cleanupService.CleanupAllAsync();
    task.Wait(TimeSpan.FromSeconds(10));  // timeout 10s
    base.OnExit(e);
}
```

### Config
- `cleanup_on_close`: AppSettings (default "true")
- Se "false" → skip CleanupAllAsync no shutdown (usuário quer manter cache)
- CleanupOrphansAsync sempre roda (crash recovery independente da config)

### Thread Safety
- CleanupAllAsync pode rodar enquanto player está ativo
  - Verificar `PlaybackEngine.State != Stopped` antes de deletar
  - Verificar `CastingService.State != Streaming` antes de deletar
  - Se em uso: skip arquivo + log warning
- Lock: `SemaphoreSlim(1,1)` para evitar cleanup concorrente

### Processed Folder
- Default: `%AppData%/CATRA/processed/`
- Configurável via AppSettings `processed_folder`
- Criar pasta se não existe (startup)
- Naming: `{episodeId}_{profile}.mp4`

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa (testes com temp folder)
- [ ] Fechar app → todos os processados deletados
- [ ] Log mostra quantidade e espaço liberado
- [ ] Crash (kill process) → reabrir → órfãos limpos
- [ ] Arquivo em uso (playing) → não deletado, log warning
- [ ] `cleanup_on_close = false` → mantém arquivos
- [ ] Cleanup não bloqueia shutdown por mais de 10s
- [ ] ProcessedFolder vazio após cleanup (verificado)
- [ ] DB limpo após cleanup (ProcessedFile + ProcessJob vazias)

## Dependências
- ST-18 (SlidingWindowService — cancela jobs antes do cleanup)
- ST-02 (ProcessedFile, ProcessJob repositories)

## Notas
- `task.Wait(10s)` no OnExit é aceitável — app está fechando
- Alternativa: `Application.Current.Exit` event + `CancelEventArgs`
  - Mas WPF não permite cancelar exit de forma confiável
- Se cleanup falhar completamente (disco travado): log + seguir
  - Próximo startup limpa via CleanupOrphansAsync
- Não usar `Environment.Exit` — deixa WPF fazer shutdown gracioso

# Subtask 02: SQLite + Modelos de Dados + Repositories

## Contexto
Spec: `.agents/specs/catra-media-player.md` (seção Modelagem de Dados)
Depende de ST-01 (estrutura de camadas).

## Objetivo
Camada de dados completa: schema SQLite, entidades, repositories com CRUD,
WAL mode, e testes de integração.

## Escopo
### Arquivos a Criar
- `src/CATRA.Data/Database/DatabaseInitializer.cs` — cria/migra schema
- `src/CATRA.Data/Database/DatabaseConnection.cs` — factory SQLite (WAL)
- `src/CATRA.Data/Entities/CategoryEntity.cs`
- `src/CATRA.Data/Entities/MediaItemEntity.cs`
- `src/CATRA.Data/Entities/EpisodeEntity.cs`
- `src/CATRA.Data/Entities/ProcessedFileEntity.cs`
- `src/CATRA.Data/Entities/ProcessJobEntity.cs`
- `src/CATRA.Data/Entities/WatchStateEntity.cs`
- `src/CATRA.Data/Entities/AppSettingEntity.cs`
- `src/CATRA.Data/Repositories/CategoryRepository.cs`
- `src/CATRA.Data/Repositories/MediaItemRepository.cs`
- `src/CATRA.Data/Repositories/EpisodeRepository.cs`
- `src/CATRA.Data/Repositories/ProcessedFileRepository.cs`
- `src/CATRA.Data/Repositories/ProcessJobRepository.cs`
- `src/CATRA.Data/Repositories/WatchStateRepository.cs`
- `src/CATRA.Data/Repositories/AppSettingsRepository.cs`
- `src/CATRA.Core/Models/Category.cs` — domain model
- `src/CATRA.Core/Models/MediaItem.cs`
- `src/CATRA.Core/Models/Episode.cs`
- `src/CATRA.Core/Models/ProcessedFile.cs`
- `src/CATRA.Core/Models/ProcessJob.cs`
- `src/CATRA.Core/Models/WatchState.cs`
- `src/CATRA.Core/Enums/MediaType.cs` — Series, Movie
- `src/CATRA.Core/Enums/ProcessProfile.cs` — Local, Dlna
- `src/CATRA.Core/Enums/JobStatus.cs` — Queued, Processing, Completed, Failed, Cancelled
- `src/CATRA.Core/Enums/ProcessStep.cs` — Decode, Interp, Upscale, Encode
- `src/CATRA.Core/Interfaces/IRepository.cs`
- `src/CATRA.Core/Interfaces/ICategoryRepository.cs`
- `src/CATRA.Core/Interfaces/IMediaItemRepository.cs`
- `src/CATRA.Core/Interfaces/IEpisodeRepository.cs`
- `src/CATRA.Core/Interfaces/IProcessedFileRepository.cs`
- `src/CATRA.Core/Interfaces/IProcessJobRepository.cs`
- `src/CATRA.Core/Interfaces/IWatchStateRepository.cs`
- `src/CATRA.Core/Interfaces/IAppSettingsRepository.cs`
- `tests/CATRA.Data.Tests/RepositoryTests.cs`

### Arquivos a Modificar
- `src/CATRA.App/App.xaml.cs` — registrar repositories no DI

### Arquivos NÃO tocar
- `src/CATRA.UI/` — sem UI nesta subtask
- `src/CATRA.Services/` — sem lógica de negócio

## Requisitos Técnicos
- `sqlite-net-pcl` (ORM leve) ou `Microsoft.Data.Sqlite` (ADO.NET) — decidir e documentar
- WAL mode habilitado na conexão: `PRAGMA journal_mode=WAL`
- Schema idêntico ao da spec (todas as 7 tabelas)
- Entities mapeiam 1:1 com tabelas; Models são domain objects limpos
- Repositories expõem: `GetAll`, `GetById`, `Insert`, `Update`, `Delete` + queries específicas
- Queries específicas necessárias:
  - `IEpisodeRepository.GetUnwatchedByMediaItem(mediaItemId)` — ORDER BY EpisodeNumber
  - `IEpisodeRepository.GetByMediaItem(mediaItemId)` — todos de uma série
  - `IWatchStateRepository.GetByEpisodeId(episodeId)`
  - `IWatchStateRepository.GetInProgress()` — progress > 0, watched = 0
  - `IProcessedFileRepository.GetByEpisodeAndProfile(episodeId, profile)`
  - `IProcessJobRepository.GetActiveByMediaItem(mediaItemId)` — queued/processing
  - `IAppSettingsRepository.Get(key)` / `Set(key, value)`
- Database file: `%AppData%/CATRA/catra.db` (configurável)
- Migrations: versão no schema, aplicar na inicialização

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa com testes de CRUD para todas as 7 tabelas
- [ ] Schema criado automaticamente na primeira execução
- [ ] WAL mode confirmado via PRAGMA
- [ ] Queries específicas testadas (unwatched, in-progress, by profile)
- [ ] DI resolve todos os repositories

## Dependências
- ST-01 (estrutura de camadas + DI)

## Notas
- AppSettings keys da spec: root_folder, processed_folder, window_size, cleanup_on_close,
  local_target_fps, local_target_width, local_target_height, local_encode_bitrate_kbps,
  dlna_target_fps, dlna_target_width, dlna_target_height, dlna_encode_bitrate_kbps,
  interp_method, upscale_method, default_skip_intro_sec, theme_override
- Inserir defaults na primeira execução

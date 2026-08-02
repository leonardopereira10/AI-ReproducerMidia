# Subtask 03: Scanner de Biblioteca + Parser de Nomes

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-01, RF-02, RN-01, RN-06)
Depende de ST-02 (modelos + repositories).

## Objetivo
Serviço que escaneia a pasta raiz, identifica categorias/séries/filmes/episódios,
aplica parser de nomes (4 padrões) e persiste no SQLite. Scan incremental +
FileSystemWatcher para mudanças em tempo real.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Library/LibraryScanner.cs` — orquestra scan completo
- `src/CATRA.Services/Library/FilenameParser.cs` — regex dos 4 padrões
- `src/CATRA.Services/Library/FilenameParserResult.cs` — resultado do parse
- `src/CATRA.Services/Library/LibraryWatcher.cs` — FileSystemWatcher wrapper
- `src/CATRA.Services/Library/MediaTypeDetector.cs` — série vs filme (RN-01)
- `src/CATRA.Services/Library/NameNormalizer.cs` — RN-06 (underscore, title case)
- `src/CATRA.Core/Interfaces/ILibraryScanner.cs`
- `src/CATRA.Core/Interfaces/IFilenameParser.cs`
- `src/CATRA.Core/Interfaces/ILibraryWatcher.cs`
- `tests/CATRA.Services.Tests/Library/FilenameParserTests.cs`
- `tests/CATRA.Services.Tests/Library/LibraryScannerTests.cs`
- `tests/CATRA.Services.Tests/Library/NameNormalizerTests.cs`

### Arquivos a Modificar
- `src/CATRA.App/App.xaml.cs` — registrar serviços no DI

### Arquivos NÃO tocar
- `src/CATRA.UI/` — sem UI
- `src/CATRA.Data/` — já pronto da ST-02

## Requisitos Técnicos

### FilenameParser (RF-02)
- 4 padrões regex da spec:
  - P1: `^\[.*?\]\[(.+?)\]\s*-\s*Epis[óo]dio\s*(\d+)` → nome + episódio
  - P2: `^\[.*?\]\s*(.+?)\s*-\s*Epis[óo]dio\s*(\d+)` → nome + episódio
  - P3: `(\d{2})EP(\d{2})` → temporada + episódio
  - P4: fallback → nome da pasta + metadata do container
- Prioridade: P1 > P2 > P3 > P4
- Retorna: `FilenameParserResult { SeriesName?, SeasonNumber?, EpisodeNumber?, RawName, PatternUsed }`
- Se nenhum padrão casa → `PatternUsed = Fallback`, `RawName = filename`

### NameNormalizer (RN-06)
- `_` → `'` (possessivo: `Mortal_s` → `Mortal's`)
- Remover tags `[Site]` do display
- Title Case para display
- Manter `RawFolderName` intacto no model

### LibraryScanner (RF-01)
- Input: pasta raiz (de AppSettings `root_folder`)
- Estrutura: `Raiz/Categoria/Série ou Filme/arquivos`
- Nível 1 = Category, Nível 2 = MediaItem, Nível 3 = Episodes
- Formatos: `.mp4`, `.avi`, `.mkv` (case-insensitive)
- MediaTypeDetector: 1 arquivo → movie, 2+ → series
- Scan incremental: comparar com DB, só processar novos/alterados/removidos
- Para cada arquivo: extrair metadata básico via FFmpeg (duração, fps, resolução)
  - Nesta subtask: usar `ffprobe` via Process (não FFmpeg.AutoGen ainda)
  - FileHash: SHA-256 do arquivo (para detectar mudanças)
- Persistir via repositories (Category, MediaItem, Episode)
- Progress reporting via `IProgress<ScanProgress>`

### LibraryWatcher (RF-01)
- FileSystemWatcher na pasta raiz (recursivo)
- Eventos: Created, Deleted, Renamed, Changed
- Debounce 2s (evitar múltiplos eventos por operação)
- Ao detectar mudança → trigger scan incremental
- IDisposable para cleanup

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa
- [ ] Parser extrai corretamente dos 4 padrões (testes com exemplos reais da spec)
- [ ] Normalizador converte `Mortal_s` → `Mortal's`, remove `[Site]`, title case
- [ ] Scanner popula DB com estrutura Categoria > Série > Episódios
- [ ] Filme (1 arquivo) vs Série (2+ arquivos) detectado corretamente
- [ ] Scan incremental não duplica registros
- [ ] FileSystemWatcher detecta novo arquivo e atualiza DB
- [ ] Metadata (duração, fps, resolução) extraído via ffprobe

## Dependências
- ST-02 (modelos + repositories)

## Notas
- Exemplos reais para testes do parser:
  - `[AniDong][A Record of a Mortal_s Journey] - Episódio 26.mp4`
  - `[AnimeFire.io] Saikyou Degarashi Ouji no Anyaku Teii Arasoi - Episódio 4 (HD).mp4`
  - `ACSADRGT01EP07.mp4`
  - `SUACLPLNDRS.mp4`
- ffprobe deve estar disponível no PATH ou bundled em `lib/ffmpeg/`
- FileHash SHA-256 pode ser lento para arquivos grandes (1.6GB) — considerar
  hash parcial (primeiros 1MB + últimos 1MB + filesize) como otimização futura

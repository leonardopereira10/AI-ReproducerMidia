# Sprint Log — CATRA Fase 1 + Fase 2

**Início:** 2026-08-01
**Modo:** loop de subagents (dev → review → QA → commit por subtask)
**Paralelismo máx:** 2

## Status

| ST | Título | Dev | Status | Review | QA | Commit | Retries |
|----|--------|-----|--------|--------|----|--------|---------|
| ST-01 | Setup WPF + camadas + DI | developer-baixo | ✅ | ✅ | ✅ | 7bb60bf | 0 |
| ST-02 | SQLite + modelos + repos | developer-medio | ✅ | ✅ | ✅ | 31aad35 | 1 (timeout) |
| ST-03 | Scanner + parser | developer-alto | ✅ | 🔄 | 🔄 | — | 1 (timeout) |
| ST-10 | Theme light/dark | developer-baixo | ⏳ | — | — | — | 0 |
| ST-04 | UI Home + Detail | developer-alto | ⏳ | — | — | — | 0 |
| ST-05 | FFmpeg decode + render | developer-critical | ⏳ | — | — | — | 0 |
| ST-06 | Player controls | developer-alto | ⏳ | — | — | — | 0 |
| ST-09 | Thumbnails | developer-medio | ⏳ | — | — | — | 0 |
| ST-07 | Watch state | developer-medio | ⏳ | — | — | — | 0 |
| ST-08 | DLNA casting | developer-alto | ⏳ | — | — | — | 0 |
| ST-11 | Settings UI | developer-medio | ⏳ | — | — | — | 0 |
| ST-12..22 | Fase 2 | — | ⏳ | — | — | — | 0 |

## Lotes (Fase 1)
1. ST-01 (single) ✅
2. ST-02 (single) ✅
3. ST-03 + ST-10 — **sequencial** (conflito App.xaml.cs DI); ST-03 ✅ dev, ST-10 ⏳
4. ST-04 + ST-05 (paralelo)
5. ST-06 + ST-09 (paralelo)
6. ST-07 + ST-08 (paralelo)
7. ST-11 (single)

## Eventos
- ST-02: 1ª execução expirou (480s) com testes falhando (sqlite-net index conflict + PRAGMA). Retry fixou `[Indexed(Unique=true)]` em WatchStateEntity. 23/23 testes.
- ST-03: 1ª execução expirou (900s) com ~90% pronto. Retry completou testes (LibraryScannerTests 26, FfprobeOutputParserTests 9) + fix double-count EpisodesRemoved + MediaItem.Title vazio em pastas P1. 86/86 testes, estável 3x.
- AMBIENTE: ffprobe NÃO está no PATH → ST-03 abstraído em IMediaProbeService (stub nos testes). Binário real será bundlado em ST-05 (FFmpeg.AutoGen).
- Lote 3 será sequencial: ST-03 e ST-10 ambos modificam App.xaml.cs (DI) — paralelismo causaria conflito no working tree.

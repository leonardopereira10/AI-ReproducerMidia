# Sprint Log — CATRA Fase 1 + Fase 2

**Início:** 2026-08-01
**Modo:** loop de subagents (dev → review → QA → commit por subtask)
**Paralelismo máx:** 2 | Testes atuais: 116 (Core 2 + Data 20 + Services 94)

## Status

| ST | Título | Dev | Status | Review | QA | Commit | Retries |
|----|--------|-----|--------|--------|----|--------|---------|
| ST-01 | Setup WPF + camadas + DI | developer-baixo | ✅ | ✅ | ✅ | 7bb60bf | 0 |
| ST-02 | SQLite + modelos + repos | developer-medio | ✅ | ✅ | ✅ | 31aad35 | 1 |
| ST-03 | Scanner + parser | developer-alto | ✅ | ✅ | ✅ | 716c2a0 | 1 |
| ST-10 | Theme light/dark | developer-baixo | ✅ | ✅ | ✅ | 72fed03 | 0 |
| ST-04 | UI Home + Detail | developer-alto | ✅ | ✅ (re-review) | ✅ | 507cd73 | 1 (rejeitado→fix) |
| ST-05 | FFmpeg decode + render | developer-critical | 🔄 | — | — | — | 0 |
| ST-06 | Player controls | developer-alto | ⏳ | — | — | — | 0 |
| ST-09 | Thumbnails | developer-medio | ⏳ | — | — | — | 0 |
| ST-07 | Watch state | developer-medio | ⏳ | — | — | — | 0 |
| ST-08 | DLNA casting | developer-alto | ⏳ | — | — | — | 0 |
| ST-11 | Settings UI | developer-medio | ⏳ | — | — | — | 0 |
| ST-12..22 | Fase 2 | — | ⏳ | — | — | — | 0 |

## Lotes (Fase 1)
1. ST-01 ✅ | 2. ST-02 ✅ | 3. ST-03 ✅ + ST-10 ✅ (sequencial)
4. ST-04 ✅ → ST-05 🔄 (sequencial — App.xaml.cs)
5. ST-06 + ST-09 | 6. ST-07 + ST-08 | 7. ST-11

## Eventos
- ST-02: retry (timeout) — fix `[Indexed(Unique=true)]` WatchStateEntity.
- ST-03: retry (timeout) — completou testes + fix double-count/prune. ffprobe abstraído (IMediaProbeService, stub).
- ST-04: 1ª exec excedeu turn budget mas COMPLETOU (116 testes). Review REJEITOU (blocker: context menu EpisodeCardControl — PlacementTarget=Button sem DPs). Fix BindingProxy + minors (leak/double-load/dead code). Re-review APROVOU.
- ST-05: crítico; ambiente headless sem GPU/ffmpeg → testes com fakes, playback real = validação manual. DLLs FFmpeg via scripts/download-ffmpeg.ps1 (lib/ffmpeg/ gitignorado). Budget 60 turns.
- Padrão observado: subtasks altas/críticas estouram turn budget (~30) — usando turnBudget 60 e retries de continuação.
- Lotes "paralelos" executados sequencialmente quando há conflito de arquivo compartilhado (App.xaml.cs DI).

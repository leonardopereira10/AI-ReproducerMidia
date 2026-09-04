# Roadmap original de construção do CATRA (ST-01 … ST-28)

> Síntese consolidada do backlog original (`.agents/specs/subtasks/ST-*.md`,
> removido). Detalhes de execução por sprint em `.agents/Learning/sprints/`.
> Spec-mãe do produto: `.agents/specs/catra-media-player.md`.

## Fase 1 — MVP (Core App + Playback + DLNA)

| ST | Entrega | Caminho principal resultante |
|----|---------|------------------------------|
| 01 | Setup WPF + camadas + DI | estrutura `src/CATRA.*` |
| 02 | SQLite + modelos + repositories | `src/CATRA.Data` |
| 03 | Scanner de biblioteca + parser de nomes | `src/CATRA.Data` (scan/parse) |
| 04 | UI Home (cards) + Detail (episódios) | `src/CATRA.UI` views |
| 05 | FFmpeg decode D3D11VA + render DX11 (HwndHost), áudio WASAPI | `src/CATRA.Services/Playback` |
| 06 | Player controls customizados | `src/CATRA.UI` |
| 07 | Watch state + threshold + "continuar assistindo" | `WatchStateService` |
| 08 | DLNA casting raw | `src/CATRA.Services/Casting` |
| 09 | Thumbnails via FFmpeg frame capture | `ThumbnailService` |
| 10 | Tema light/dark seguindo Windows | `ThemeService` |
| 11 | Settings UI | `SettingsViewModel` |

## Fase 2 — GPU Bridge e processamento

| ST | Entrega | Caminho principal resultante |
|----|---------|------------------------------|
| 12 | Native bridge C++ skeleton (C ABI, `GuardCabi`) | `native/catra-gpu/catra_gpu.*` |
| 13 | RIFE v4 interpolação (ORT: DirectML → CPU) | `interp_rife.*` |
| 14 | FSR4 upscale via FFX runtime-loaded (fallback FSR1) | `upscale_fsr4.*`, `upscale_fsr1.*`, `ffx_runtime.*` |
| 15 | Interop D3D11↔DX12 (NT shared handles, keyed mutex, pool) | `d3d_interop.*` |
| 16 | AMF H.265 encoder (opcional em build) | `encode_amf.*` |
| 17 | Orquestração do pipeline em C# | `ProcessingPipeline.cs` |
| 18 | Janela deslizante (queue + rotação de episódios) | `SlidingWindowService`, `ProcessingQueueService` |
| 19 | UI de pré-processamento (radio-switch + fila) | `ProcessingQueueViewModel` |
| 20 | Playback/DLNA usam arquivo processado | `ProcessedFile` + resolução de mídia |
| 21 | Cleanup on close/startup | `ProcessedFileUsage` |
| 22 | Settings de processamento + perfis | `AppSettingsModel`, perfis |

## Fase 3 — stubs (registrados, não implementados à época)

| ST | Tema | Status histórico |
|----|------|------------------|
| 23 | Metadados online (TMDB/AniList) | stub |
| 24 | Legendas MKV/.srt | stub |
| 25 | Busca/filtro biblioteca | entregue depois (Web Panel Streaming Home) |
| 26 | Reprocessamento automático por hash | stub |
| 27 | Controle remoto Bluetooth | stub |
| 28 | Múltiplos perfis de usuário | stub |

## Sprints posteriores (arquivados em `.agents/Learning/sprints/`)

| Sprint | Tema | Doc consolidado |
|--------|------|-----------------|
| 01–03 | construção inicial (ver `SPRINT_LOG.md` de cada) | — |
| 04 | GPU pipeline: FSR3 FG playback, bugfixes decode/sync | — |
| 05 | Web Control Panel + Streaming Home | `docs/sprints/SPRINT_05_web_panel.md` |

## Padrões herdados que continuam válidos

- C ABI plano no native; toda entrada envolta em `GuardCabi`.
- Texturas devolvidas ao caller são liberadas por ele (`catra_release_texture`).
- Keyed mutex ping-pong 0/1 + `Flush()` obrigatório antes de `ReleaseSync`.
- Fallbacks em cascata (hardware → software) nunca lançam exceção ao usuário.

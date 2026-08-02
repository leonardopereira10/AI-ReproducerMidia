# CATRA — Backlog de Subtasks

**Spec principal:** `.agents/specs/catra-media-player.md`

## Fase 1 — MVP (Core App + Playback + DLNA Raw)

| # | Subtask | Depende de | Complexidade |
|---|---|---|---|
| ST-01 | Setup WPF + camadas + DI | — | baixa |
| ST-02 | SQLite + modelos + repositories | ST-01 | media |
| ST-03 | Scanner de biblioteca + parser de nomes | ST-02 | alta |
| ST-04 | UI Home (cards) + Detail (episódios) | ST-02, ST-03 | alta |
| ST-05 | FFmpeg decode D3D11VA + render DX11 | ST-01 | critical |
| ST-06 | Player controls customizados | ST-05 | alta |
| ST-07 | Watch state + threshold + continuar | ST-02, ST-06 | media |
| ST-08 | DLNA casting raw | ST-05 | alta |
| ST-09 | Thumbnails (FFmpeg frame capture) | ST-02, ST-05 | media |
| ST-10 | Theme Windows (light/dark) | ST-01 | baixa |
| ST-11 | Settings UI | ST-02, ST-04 | media |

## Fase 2 — Pre-Processamento GPU

| # | Subtask | Depende de | Complexidade |
|---|---|---|---|
| ST-12 | Native bridge C++ skeleton | ST-01 | alta |
| ST-13 | RIFE v4 integration | ST-12 | critical |
| ST-14 | FSR 4 SDK integration | ST-12 | critical |
| ST-15 | D3D11→DX12 texture interop | ST-12, ST-05 | alta |
| ST-16 | AMF H.265 encoder | ST-12 | alta |
| ST-17 | Pipeline orchestration C# | ST-13..16 | critical |
| ST-18 | Janela deslizante (queue + rotação) | ST-17, ST-07 | alta |
| ST-19 | UI pre-processar (radio-switch + fila) | ST-18 | media |
| ST-20 | Playback/DLNA usar processado | ST-17, ST-06, ST-08 | media |
| ST-21 | Cleanup on close + startup | ST-18 | baixa |
| ST-22 | Settings processamento + perfis | ST-19 | baixa |

## Fase 3 — Enhancements (stubs)

| # | Subtask | Depende de | Complexidade |
|---|---|---|---|
| ST-23 | Metadados online (TMDB/AniList) | ST-04 | media |
| ST-24 | Legendas MKV + .srt | ST-06 | media |
| ST-25 | Busca/filtro biblioteca | ST-04 | baixa |
| ST-26 | Reprocessamento automático (hash) | ST-18 | media |

## Fase 4 — Futuro (stubs)

| # | Subtask | Depende de | Complexidade |
|---|---|---|---|
| ST-27 | Bluetooth controle remoto | ST-06 | alta |
| ST-28 | Múltiplos perfis | ST-07 | media |

## Grafo de Dependências (Fase 1)

```
ST-01 ──┬── ST-02 ──┬── ST-03 ── ST-04 ──┬── ST-11
        │           │                     │
        │           ├── ST-07 ◄── ST-06 ◄─┤
        │           │                     │
        │           └── ST-09 ◄───────────┤
        │                                 │
        ├── ST-05 ──┬── ST-06             │
        │           ├── ST-08             │
        │           └── ST-09             │
        │                                 │
        └── ST-10                         │
                                          │
        ST-05 ────── ST-08 ───────────────┘
```

## Grafo de Dependências (Fase 2)

```
ST-12 ──┬── ST-13 ──┐
        ├── ST-14 ──┤
        ├── ST-15 ──┼── ST-17 ──┬── ST-18 ──┬── ST-19 ── ST-22
        └── ST-16 ──┘           │           │
                                ├── ST-20   └── ST-21
                                │
                    ST-06+ST-08 ┘
```

# SPRINT 01 — CATRA Fase 1 (MVP) — CONCLUÍDO

**Resultado:** APROVADO (qa-tester-alto validação final)
**Tag:** v0.1.0
**Testes:** 290 (Core 2 + Data 20 + Services 202 + UI 66) — Release 0w/0e

## Subtasks (todas dev → review → QA → commit)
| ST | Título | Commit | Retries | Notas |
|----|--------|--------|---------|-------|
| ST-01 | Setup WPF + camadas + DI | 7bb60bf | 0 | |
| ST-02 | SQLite + modelos + repos | 31aad35 | 1 | timeout; fix Indexed(Unique) |
| ST-03 | Scanner + parser | 716c2a0 | 1 | timeout; ffprobe abstraído |
| ST-10 | Theme light/dark | 72fed03 | 0 | |
| ST-04 | UI Home + Detail | 507cd73 | 1 | rejeitado→fix BindingProxy context menu |
| ST-05 | FFmpeg decode + render | 29eeb6a | 1 | timeout; 3 majors fixados (áudio pós-seek, EAGAIN, letterbox) |
| ST-06 | Player controls | 4ddff6c | 1 | timeout; +39 UI tests |
| ST-07 | Watch state | 091d9d2 | 0 | RN-02 threshold |
| ST-08 | DLNA casting | 9bb31c7 | 1 | timeout; Kestrel Range testado real |
| ST-11 | Settings UI | 151740a | 0 | |
| ST-09 | Thumbnails | da019ed | 1 | rejeitado→fix custom cover via cache |

## Validação manual pendente (inerente hardware)
- RF-05 playback real (D3D11VA/DX11/WASAPI com vídeo real) — requer GPU + binários FFmpeg (scripts/download-ffmpeg.ps1)
- RF-06 DLNA com Samsung TV real
- RF-08 extração real de thumbnails (binário ffmpeg)

## Lições
- Subtasks alta/crítica estouram turn budget (~30) → usar turnBudget 50-60 + retries de continuação
- Conflito App.xaml.cs (DI) → lotes "paralelos" viraram sequenciais
- Reviewer com task extenso pode travar → tasks focados/time-boxed
- Ambiente headless: abstrair tudo (ffprobe/ffmpeg/casting/GPU) atrás de interfaces com fakes nos testes

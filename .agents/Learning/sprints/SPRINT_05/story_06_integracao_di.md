# Story 06 — Integração, DI e Proteção de Arquivos

**Tipo:** dev
**Complexidade:** Média
**Espec:** `.agents/specs/web_panel_streaming_home.md` — Seções 6 e D7

## Descrição

Registrar todos os novos serviços no DI container, wire-up na inicialização do app,
exibir URL do painel no WPF, servir thumbnails/posters via API, estender
`ProcessedFileUsage` para proteger arquivos em streaming, e implementar token lifecycle.

## Critérios de Aceite

- [ ] `IStreamService` e `ILibraryApiService` registrados no DI
- [ ] Wire-up no `App.xaml.cs` / `MainWindow` (inicialização na startup)
- [ ] URL do painel visível no app WPF (status bar, porta de `AppSettingsModel.WebPanelPort`)
- [ ] Thumbnails/posters servidos via `/api/thumbnail/{id}` e `/api/poster/{id}` (corrigir path cru)
- [ ] `ProcessedFileUsage` estendido: decorator que inclui sessões de streaming browser
- [ ] Token lifecycle: unregister on episode change, session end, TTL cleanup (5min inatividade)
- [ ] `movflags +faststart` adicionado ao `AudioMuxer` para MP4 processados
- [ ] Regressão: DLNA e playback local continuam funcionando

## Dependências

- Story 01, 02, 03 (serviços a registrar)
- Todas as stories anteriores para integração final

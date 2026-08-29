# Subtask 16: URL do Painel no WPF + Thumbnails/Posters

**Story:** story_06_integracao_di.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Exibir URL do painel web no app WPF e servir thumbnails/posters via API.

## Arquivos Alvo
- `src/CATRA.App/MainWindow.xaml` (modificar — status bar com URL)
- `src/CATRA.App/MainWindow.xaml.cs` (modificar — exibir URL)
- `src/CATRA.Services/WebControl/WebControlServer.cs` (modificar — endpoints thumbnail/poster)

## Passos
1. **URL no WPF**: Adicionar TextBlock na status bar mostrando `http://{IP}:{Port}`
   - IP via `IMediaHttpServer.GetLocalIp()` (reutilizar)
   - Porta via `AppSettingsModel.WebPanelPort` (não hardcoded 5050)
   - Atualizar quando servidor iniciar
2. **Thumbnails**: Endpoint `/api/thumbnail/{episodeId}`
   - Buscar `Episode.ThumbnailPath` via `IEpisodeRepository`
   - Servir arquivo de imagem (image/jpeg) com byte-range
3. **Posters**: Endpoint `/api/poster/{itemId}`
   - Buscar `MediaItem.CoverPath` via `IMediaItemRepository`
   - Servir arquivo de imagem
4. **Corrigir `ResolveThumbnailUrl`**: em `WebControlService`, mudar de path cru para `/api/thumbnail/{id}`

## Critérios de Aceite
- [ ] URL visível no WPF com IP LAN e porta configurável
- [ ] Thumbnails servidos via API (não path de filesystem)
- [ ] Posters servidos via API
- [ ] WebControlState.ThumbnailUrl usa `/api/thumbnail/{id}`

## Dependências
- subtask_15 (DI)

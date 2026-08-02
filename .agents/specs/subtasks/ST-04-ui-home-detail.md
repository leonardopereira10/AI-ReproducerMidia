# Subtask 04: UI Home (Cards) + Detail (Episódios)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Telas 1 e 2, RF-01, RF-07)
Depende de ST-02 (models) e ST-03 (scanner popula DB).

## Objetivo
Duas telas principais: Home com cards por categoria e Detail com episódios
separados em assistidos/não-assistidos. Navegação entre elas. Context menu
para marcar assistido.

## Escopo
### Arquivos a Criar
- `src/CATRA.UI/Views/HomeView.xaml` + `.cs`
- `src/CATRA.UI/Views/MediaDetailView.xaml` + `.cs`
- `src/CATRA.UI/ViewModels/HomeViewModel.cs`
- `src/CATRA.UI/ViewModels/MediaDetailViewModel.cs`
- `src/CATRA.UI/Controls/MediaCardControl.xaml` + `.cs` — card reutilizável
- `src/CATRA.UI/Controls/EpisodeCardControl.xaml` + `.cs` — card de episódio
- `src/CATRA.UI/Converters/BoolToVisibilityConverter.cs`
- `src/CATRA.UI/Converters/ProgressToBadgeConverter.cs`
- `src/CATRA.Core/Interfaces/ILibraryService.cs` — facade p/ UI consultar biblioteca

### Arquivos a Modificar
- `src/CATRA.Services/Library/LibraryService.cs` — criar (implementa ILibraryService)
- `src/CATRA.App/App.xaml.cs` — registrar views/viewmodels no DI + navigation

### Arquivos NÃO tocar
- `src/CATRA.Data/` — já pronto
- `src/CATRA.Services/Library/LibraryScanner.cs` — não modificar (ST-03)

## Requisitos Técnicos

### HomeView (Tela 1)
- Tabs ou sidebar com categorias (vindas de `CategoryRepository.GetAll`)
- Tab "Todos" mostra tudo
- Grid de cards (WrapPanel ou UniformGrid responsivo)
- Cada card (`MediaCardControl`):
  - Capa/thumbnail (placeholder se não existe — ST-09 gera)
  - Título normalizado
  - Tipo: "12 eps" ou "Filme"
  - Progresso: "▶ 3/12" (3 assistidos de 12) ou "✓" (todos assistidos)
  - Badge "Continuar Assistindo" se RN-03 aplica
- Clique no card → navega para MediaDetailView
- Botão "🔄 Atualizar" na toolbar → trigger LibraryScanner.ScanAsync
- Botão "⚙️" → navega para Settings (ST-11, placeholder por agora)

### MediaDetailView (Tela 2)
- Header: botão voltar, título, capa grande
- Info: ano, gênero (placeholder até Fase 3), skip intro configurável
- Seção "Pre-Processar" (placeholder — ST-19 implementa):
  - Radio-switch `[Local | DLNA]` + botão "Iniciar" (disabled por agora)
  - Label "Janela: 5 episódios | ~XX GB estimados"
- Seção "Não Assistidos": grid de EpisodeCardControl
- Seção "Assistidos": grid de EpisodeCardControl (visualmente distintos, opacidade)
- Cada `EpisodeCardControl`:
  - Thumbnail (placeholder)
  - "EP01" + display title
  - Badge de status: "⚙ fila" / "✓ pronto" / "○" (placeholder — ST-19)
  - Badge "▶" se tem progresso (continuar assistindo)
- Clique no episódio → abre player (ST-06, placeholder por agora)
- Context menu (right-click):
  - "Marcar como assistido" / "Desmarcar"
  - "Renomear" (dialog inline)
  - "Configurar capa" (file picker)
  - "Detalhes" (dialog com info do arquivo)

### LibraryService
- Facade que UI usa para consultar dados:
  - `GetCategoriesAsync()` → List<Category> com contagem de MediaItems
  - `GetMediaItemsAsync(categoryId?)` → List<MediaItem> com progresso
  - `GetEpisodesAsync(mediaItemId)` → List<Episode> com WatchState
  - `ToggleWatchedAsync(episodeId)` → marca/desmarca
  - `GetContinueWatchingAsync()` → episódios com RN-03
- Usa repositories internamente
- Notifica UI via eventos/INotifyPropertyChanged quando scan completa

### MVVM
- CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]`
- ViewModels não referenciam Views
- Navigation via `INavigationService` (interface em Core, impl em App)

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Home exibe cards agrupados por categoria
- [ ] Tab "Todos" funciona
- [ ] Clique no card navega para Detail
- [ ] Detail lista episódios separados (assistidos / não assistidos)
- [ ] Context menu marca/desmarca assistido (persiste no DB)
- [ ] Badge "Continuar Assistindo" aparece quando RN-03 aplica
- [ ] Botão "Atualizar" dispara scan e refresha UI
- [ ] Botão voltar funciona
- [ ] Layout responsivo (cards reflow com resize)

## Dependências
- ST-02 (models + repositories)
- ST-03 (scanner popula DB com dados)

## Notas
- Thumbnails usam placeholder (imagem genérica) até ST-09
- Player é placeholder (MessageBox "Player - ST-06") até ST-06
- Pre-Processar é placeholder (disabled) até ST-19
- Settings é placeholder (MessageBox) até ST-11

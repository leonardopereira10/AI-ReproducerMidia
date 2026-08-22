# Subtask 07: Integração com CATRA.App — DI + UI + Settings

**Story:** story_07_integracao_catra.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alto

## Descrição
Integrar o WebControlService no CATRA.App: DI, startup automático, URL na barra de status, settings.

## Arquivos Alvo (fileScope)
- `src/CATRA.App/App.xaml.cs` (EDIT — registrar DI + start/stop server)
- `src/CATRA.App/MainWindow.xaml` (EDIT — barra de status com URL)
- `src/CATRA.App/MainWindow.xaml.cs` (EDIT — atualizar URL dinamicamente)
- `src/CATRA.Core/Models/AppSettingsModel.cs` (EDIT — adicionar WebPanelPort)
- `src/CATRA.Data/Database/DatabaseInitializer.cs` (EDIT — seed default port)
- `src/CATRA.UI/ViewModels/PlayerViewModel.cs` (EDIT — notificar WebControlHub)
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` (EDIT — setting porta do painel)

## Passos
1. **DI Registration** (App.xaml.cs ou ServiceCollection setup):
   ```csharp
   services.AddSingleton<IWebControlServer, WebControlServer>();
   services.AddSingleton<IWebControlService, WebControlService>();
   services.AddSingleton<IWebControlHub, WebSocketHandler>();
   ```

2. **Startup automático** (App.xaml.cs OnStartup):
   - Após inicialização completa, start WebControlServer
   - Capturar porta e IP para exibição
   - OnExit: StopAsync do servidor

3. **AppSettingsModel** — adicionar:
   ```csharp
   public const string WebPanelPortKey = "web_panel_port";
   public int WebPanelPort { get; set; } = 5050;
   ```
   - Load/Save no repositório

4. **DatabaseInitializer** — seed default:
   ```csharp
   { AppSettingsModel.WebPanelPortKey, "5050" }
   ```

5. **PlayerViewModel** — integrar com WebControlService:
   - Injetar `IWebControlService` no construtor
   - Quando casting inicia (OnCastingStateChanged → Streaming): chamar `webControlService.SetCurrentEpisode(episodeId)`
   - Quando casting muda de estado: o WebControlService já subscreve ICastingService diretamente
   - Para next/previous via web: o WebControlService precisa saber o episódio atual

6. **MainWindow** — barra de status:
   - Adicionar TextBlock na status bar: `📱 Painel: http://{ip}:{port}`
   - Atualizar quando server inicia
   - Click → copia URL para clipboard

7. **SettingsViewModel** — adicionar setting de porta:
   - Propriedade WebPanelPort
   - Salvar no repositório
   - Nota: mudança de porta requer restart do server (ou do app)

## Critérios de Aceite
- [ ] DI registra todos os serviços web
- [ ] WebControlServer inicia automaticamente no startup
- [ ] URL exibida na barra de status do MainWindow
- [ ] Click na URL copia para clipboard
- [ ] WebPanelPort em AppSettingsModel (default 5050)
- [ ] Seed no DatabaseInitializer
- [ ] PlayerViewModel notifica WebControlService do episódio atual
- [ ] Graceful shutdown no OnExit
- [ ] Projeto compila sem erros

## Dependências
- subtask_02 (WebControlServer)
- subtask_03 (WebControlService)
- subtask_04 (WebSocketHandler)

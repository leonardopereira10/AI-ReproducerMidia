# Story 07: Integração com CATRA.App — PlayerViewModel + UI

## Descrição
Integrar o WebControlService no CATRA.App:
- Registrar WebControlService + WebControlServer no DI
- Hook do WebControlHub no PlayerViewModel (broadcast de estado após mudanças)
- Iniciar WebControlServer automaticamente com o app (ou via toggle nas settings)
- Exibir URL de acesso na barra de status do CATRA ("📱 Painel: http://192.168.1.10:5050")
- Adicionar setting para porta do painel web

## Tipo
dev

## Critérios de Aceite
- [ ] DI registra IWebControlService, IWebControlHub, WebControlServer
- [ ] WebControlServer inicia automaticamente no startup do app
- [ ] PlayerViewModel notifica WebControlHub de mudanças de estado
- [ ] Barra de status exibe URL do painel (clicável para copiar)
- [ ] Setting "web_panel_port" em AppSettingsModel (default 5050)
- [ ] Toggle "Ativar painel web" nas Settings
- [ ] Graceful shutdown do servidor ao fechar o app
- [ ] Projeto compila sem erros

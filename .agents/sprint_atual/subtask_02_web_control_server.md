# Subtask 02: WebControlServer — Kestrel Standalone (CATRA.Services)

**Story:** story_02_web_control_server.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição
Criar o `WebControlServer` — Kestrel standalone na porta 5050 que serve arquivos estáticos, API REST e WebSocket.

## Arquivos Alvo (fileScope)
- `src/CATRA.Services/WebControl/WebControlServer.cs` (NOVO)
- `src/CATRA.Core/Interfaces/IWebControlServer.cs` (NOVO)

## Passos
1. Criar `IWebControlServer` em CATRA.Core:
   ```csharp
   bool IsRunning { get; }
   int Port { get; }
   Task StartAsync(CancellationToken ct = default);
   Task StopAsync();
   ```

2. Criar `WebControlServer` em CATRA.Services/WebControl/:
   - Usa Kestrel (WebHostBuilder) similar ao MediaHttpServer
   - Bind em `0.0.0.0:{port}` (default 5050)
   - Porta configurável via construtor
   - Pipeline: `UseWebSockets()` + middleware de roteamento
   
3. Rotas:
   - `GET /` → serve `index.html` de EmbeddedResource (content-type text/html)
   - `GET /css/style.css` → serve style.css de EmbeddedResource
   - `GET /js/app.js` → serve app.js de EmbeddedResource
   - `GET /api/state` → chama `IWebControlService.GetCurrentState()`, retorna JSON
   - `GET /api/thumbnail/{id}` → serve thumbnail do episódio (lê arquivo via IThumbnailService ou direto do path)
   - `WS /ws` → aceita WebSocket (delegado para WebSocketHandler — subtask 04)

4. EmbeddedResource:
   - Adicionar no .csproj do CATRA.Services:
     ```xml
     <EmbeddedResource Include="WebControl\wwwroot\**\*" />
     ```
   - Criar pasta `WebControl/wwwroot/` com index.html, css/style.css, js/app.js (placeholder vazio por enquanto)
   - Helper para ler EmbeddedResource por nome

5. `StartAsync` / `StopAsync` idempotentes (mesmo padrão do MediaHttpServer)

6. Método `GetLocalIp()` reutilizado (ou delegado ao IMediaHttpServer existente)

## Critérios de Aceite
- [ ] IWebControlServer com IsRunning, Port, StartAsync, StopAsync
- [ ] Kestrel escuta em 0.0.0.0:5050
- [ ] GET / retorna HTML (placeholder)
- [ ] GET /api/state retorna JSON
- [ ] WebSocket /ws aceita conexão
- [ ] EmbeddedResource configurado no csproj
- [ ] StartAsync/StopAsync idempotentes
- [ ] Projeto compila sem erros

## Dependências
- subtask_01 (IWebControlService para /api/state)

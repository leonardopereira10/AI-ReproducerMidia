# Subtask 04: WebSocket Handler — Broadcast de Estado

**Story:** story_04_websocket_handler.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição
Implementar o handler WebSocket que gerencia conexões, processa comandos e faz broadcast de estado.

## Arquivos Alvo (fileScope)
- `src/CATRA.Services/WebControl/WebSocketHandler.cs` (NOVO)

## Passos
1. Criar `WebSocketHandler`:
   - Dependências: `IWebControlService`, `IWebControlHub` (injetados)
   - Lista de conexões ativas: `ConcurrentDictionary<string, WebSocket>`
   
2. Método `HandleAsync(HttpContext context)`:
   - Verifica `context.WebSockets.IsWebSocketRequest`
   - Aceita WebSocket: `await context.WebSockets.AcceptWebSocketAsync()`
   - Adiciona na lista de conexões
   - Envia snapshot completo do estado (JSON) como primeira mensagem
   - Loop de recebimento: lê mensagens, parse JSON para WebControlCommand, chama HandleCommandAsync
   - On close: remove da lista

3. Implementa `IWebControlHub`:
   - `SendFullStateAsync(state)` → serializa para JSON, envia para todas as conexões
   - `BroadcastPositionAsync(position, duration)` → envia `{"type":"position","position":125.5,"duration":2400.0}` para todos
   - `NotifyQueueChangedAsync(queue)` → envia `{"type":"queue","items":[...]}` para todos

4. Push de posição periódico:
   - Timer (PeriodicTimer 1s) ativo quando IsPlaying
   - Envia BroadcastPositionAsync a cada tick
   - Para quando IsPaused ou Stopped

5. Subscreve eventos do IWebControlService:
   - `StateChanged` → SendFullStateAsync
   - `PositionChanged` → BroadcastPositionAsync

6. Tratamento de erros:
   - WebSocket closed unexpectedly → remove da lista, log
   - JSON parse error → ignora mensagem (não crasha)
   - Send failure → remove conexão defeituosa

7. JSON serialization:
   - Usar `System.Text.Json` com `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`
   - Tipos de mensagem: `{"type":"state","data":{...}}`, `{"type":"position","position":X,"duration":Y}`, `{"type":"queue","items":[...]}`

## Critérios de Aceite
- [ ] Aceita conexões WebSocket em /ws
- [ ] Envia snapshot na conexão inicial
- [ ] Processa comandos JSON (todos os 7 tipos)
- [ ] Broadcast para todos os clientes conectados
- [ ] Push de posição periódico (~1s) quando playing
- [ ] Cleanup de conexões fechadas
- [ ] Trata erros gracefully
- [ ] Thread-safe (ConcurrentDictionary)
- [ ] Projeto compila sem erros

## Dependências
- subtask_01 (modelos)
- subtask_02 (servidor — integra no pipeline Kestrel)
- subtask_03 (WebControlService para processar comandos)

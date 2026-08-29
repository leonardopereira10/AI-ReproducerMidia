# Subtask 10: WebSocketHandler — Routing por Client Type

**Story:** story_03_websocket_browser_mode.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alto

## Descrição

Atualizar o `WebSocketHandler` para suportar: envio de mensagens "library" e "devices",
relay de comandos para player client específico, e tracking de client IDs por conexão.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/WebSocketHandler.cs` (modificar)
- `src/CATRA.Core/Interfaces/IWebControlHub.cs` (modificar)

## Passos
1. `ClientConnection` adicionar: `string? PlayerClientId` — se esta conexão é o player client
2. `HandleAsync`: ao aceitar conexão, gerar connectionId único, registrar no dict
3. `HandleIncomingMessageAsync`: 
   - Se comando é `playEpisode`/`reportProgress`/`ended`/`ready` → marcar conexão como player client via `WebControlService.SetPlayerClient(connectionId)`
   - Se comando é `browse` → chamar `ILibraryApiService`, enviar resposta tipo "library" para ESTA conexão (não broadcast)
   - Se comando é relayável (play/pause/seek/volume) e emissor NÃO é player client → relay para player client
4. `IWebControlHub` adicionar:
   - `Task SendToPlayerClientAsync(string message)` — envia apenas para o player client
   - `Task SendToConnectionAsync(string connectionId, string message)` — envia para conexão específica
5. Subscrever a `WebControlService.CommandForPlayer` → quando fired, enviar para player client connection
6. On disconnect: se conexão desconectada é player client → `WebControlService.SetPlayerClient(null)`

## Critérios de Aceite
- [ ] Mensagens "library" enviadas apenas para conexão que solicitou browse
- [ ] Comandos relayed chegam ao player client
- [ ] Disconnect do player client limpa estado
- [ ] Broadcast de state/position continua funcionando para todos
- [ ] Build passa

## Dependências
- subtask_09 (player client management)

# Story 02: Servidor HTTP Standalone (Kestrel :5050)

## Descrição
Criar o `WebControlServer` no CATRA.Services — um Kestrel standalone na porta 5050 que serve:
- `GET /` → SPA estático (HTML+CSS+JS de recursos embutidos)
- `GET /api/state` → snapshot REST do estado atual
- `GET /api/thumbnail/{episodeId}` → thumbnail do episódio
- `WS /ws` → WebSocket bidirecional (comandos + push de estado)

## Tipo
dev

## Critérios de Aceite
- [ ] Kestrel escuta em 0.0.0.0:5050 (configurável via AppSettings)
- [ ] Serve arquivos estáticos de EmbeddedResource
- [ ] WebSocket aceita conexões e faz handshake
- [ ] Endpoint REST retorna JSON do estado atual
- [ ] Endpoint de thumbnail serve imagem do episódio
- [ ] StartAsync/StopAsync idempotentes
- [ ] Integrar com FirewallHelper para abrir porta 5050
- [ ] Projeto compila sem erros

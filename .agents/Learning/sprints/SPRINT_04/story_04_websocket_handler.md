# Story 04: WebSocket Handler — Broadcast de Estado

## Descrição
Implementar o handler WebSocket no WebControlServer que:
- Gerencia conexões de clientes (aceita, tracking, cleanup)
- Deserializa comandos JSON recebidos
- Chama WebControlService para processar cada comando
- Broadcast de estado completo para todos os clientes conectados
- Push de posição a cada ~1s quando playing
- Notifica mudanças de fila (queue changed)

## Tipo
dev

## Critérios de Aceite
- [ ] Aceita conexões WebSocket em /ws
- [ ] Envia snapshot completo do estado na conexão inicial
- [ ] Processa comandos JSON (type: play/pause/seek/volume/skipIntro/nextEpisode/previousEpisode)
- [ ] Broadcast de estado para todos os clientes após cada comando
- [ ] Push de posição periódico (~1s) quando playing
- [ ] Cleanup de conexões fechadas (dispose, remove da lista)
- [ ] Trata erros de conexão gracefully (não crasha o servidor)
- [ ] Suporta múltiplos clientes conectados (broadcast para todos)
- [ ] Projeto compila sem erros

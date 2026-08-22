# Story 06: Frontend — JavaScript (WebSocket Client + Controles)

## Descrição
Implementar a lógica JS do painel:
- Conexão WebSocket com reconexão automática
- Recebe e aplica estado (posição, duração, título, volume, etc.)
- Barra de progresso: drag/touch para seek
- Botões de transporte enviam comandos via WebSocket
- Botão "Pular Abertura" envia skipIntro
- Slider de volume envia volume
- Atualiza fila de episódios
- Indicador de conexão (reconecta automaticamente)
- Formatação de tempo (MM:SS / HH:MM:SS)

## Tipo
dev

## Critérios de Aceite
- [ ] Conecta WebSocket ao /ws do servidor
- [ ] Reconexão automática com backoff exponencial (1s, 2s, 4s, max 30s)
- [ ] Atualiza posição em tempo real (smooth seek bar)
- [ ] Seek bar: touch/mouse drag envia seek no release
- [ ] Play/Pause toggle com ícone correto
- [ ] Volume slider envia comando e atualiza visual
- [ ] SkipIntro envia comando
- [ ] Next/Previous enviam comandos
- [ ] Fila renderizada dinamicamente
- [ ] Indicador de conexão (verde = conectado, vermelho = reconectando)
- [ ] Formatação de tempo correta (MM:SS para < 1h, HH:MM:SS para >= 1h)
- [ ] Arquivo app.js como EmbeddedResource

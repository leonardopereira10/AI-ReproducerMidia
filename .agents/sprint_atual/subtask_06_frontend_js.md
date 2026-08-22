# Subtask 06: Frontend — JavaScript (WebSocket + Controles)

**Story:** story_06_frontend_js.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição
Implementar a lógica JS do painel: WebSocket client, controles interativos, atualização de estado em tempo real.

## Arquivos Alvo (fileScope)
- `src/CATRA.Services/WebControl/wwwroot/js/app.js` (NOVO/REWRITE)

## Passos
1. **WebSocket Manager**:
   ```javascript
   class CatraClient {
     constructor() {
       this.ws = null;
       this.reconnectDelay = 1000;
       this.maxReconnectDelay = 30000;
       this.state = {};
     }
     
     connect() {
       const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
       this.ws = new WebSocket(`${protocol}//${location.host}/ws`);
       this.ws.onopen = () => { this.reconnectDelay = 1000; updateConnectionStatus(true); };
       this.ws.onclose = () => { updateConnectionStatus(false); this.scheduleReconnect(); };
       this.ws.onmessage = (e) => this.handleMessage(JSON.parse(e.data));
     }
     
     scheduleReconnect() {
       setTimeout(() => this.connect(), this.reconnectDelay);
       this.reconnectDelay = Math.min(this.reconnectDelay * 2, this.maxReconnectDelay);
     }
     
     send(command) { this.ws.send(JSON.stringify(command)); }
     
     handleMessage(msg) {
       switch(msg.type) {
         case 'state': this.applyFullState(msg.data); break;
         case 'position': this.updatePosition(msg.position, msg.duration); break;
         case 'queue': this.updateQueue(msg.items); break;
       }
     }
   }
   ```

2. **Aplicar estado completo** (type: state):
   - Atualiza título, thumbnail, posição, duração, volume
   - Atualiza play/pause button icon
   - Atualiza skip intro button (label com tempo, enabled/disabled)
   - Atualiza device info + profile label
   - Atualiza next/prev buttons (enabled/disabled)
   - Atualiza fila

3. **Seek bar**:
   - `input` event: atualiza label de tempo (preview)
   - `change` event: envia comando seek `{type:"seek", position: value}`
   - `mousedown/touchstart`: marca "seeking" (ignora updates de posição)
   - `mouseup/touchend`: envia seek, limpa "seeking"

4. **Transport controls**:
   - Play/Pause: envia `{type:"play"}` ou `{type:"pause"}` (toggle baseado no estado)
   - Forward (+10s): envia `{type:"seek", position: currentPos + 10}`
   - Back (-10s): envia `{type:"seek", position: Math.max(0, currentPos - 10)}`
   - Next: envia `{type:"nextEpisode"}`
   - Previous: envia `{type:"previousEpisode"}`

5. **Skip Intro**: envia `{type:"skipIntro"}`

6. **Volume**:
   - `input` event: atualiza label
   - `change` event: envia `{type:"volume", level: value}`

7. **Formatação de tempo**:
   ```javascript
   function formatTime(seconds) {
     const h = Math.floor(seconds / 3600);
     const m = Math.floor((seconds % 3600) / 60);
     const s = Math.floor(seconds % 60);
     if (h > 0) return `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`;
     return `${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`;
   }
   ```

8. **Renderização da fila**:
   - Para cada item: card com título + duração
   - Item atual com destaque visual

9. **Connection status**:
   - Verde + "Conectado" quando ws.onopen
   - Vermelho + "Reconectando..." quando ws.onclose

## Critérios de Aceite
- [ ] WebSocket conecta ao /ws
- [ ] Reconexão automática com backoff (1s → 30s max)
- [ ] Posição atualiza em tempo real (smooth)
- [ ] Seek bar drag/touch funciona
- [ ] Play/Pause toggle correto
- [ ] Volume slider funcional
- [ ] SkipIntro envia comando
- [ ] Next/Previous funcionam
- [ ] Fila renderizada
- [ ] Connection indicator funcional
- [ ] Formatação de tempo correta

## Dependências
- subtask_05 (HTML+CSS para manipular DOM)
- subtask_04 (WebSocket server para testar integração)

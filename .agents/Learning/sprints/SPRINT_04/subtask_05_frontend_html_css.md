# Subtask 05: Frontend — HTML + CSS (Mobile-First, Dark Theme)

**Story:** story_05_frontend_html_css.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição
Criar o layout HTML+CSS do painel web — mobile-first, tema escuro, touch-friendly.

## Arquivos Alvo (fileScope)
- `src/CATRA.Services/WebControl/wwwroot/index.html` (NOVO/REWRITE)
- `src/CATRA.Services/WebControl/wwwroot/css/style.css` (NOVO/REWRITE)

## Passos
1. `index.html` — estrutura:
   ```html
   <!DOCTYPE html>
   <html lang="pt-BR">
   <head>
     <meta charset="UTF-8">
     <meta name="viewport" content="width=device-width, initial-scale=1.0, user-scalable=no">
     <title>CATRA — Painel de Controle</title>
     <link rel="stylesheet" href="/css/style.css">
   </head>
   <body>
     <!-- Connection indicator -->
     <div id="connection-status" class="disconnected">
       <span class="dot"></span> <span class="text">Desconectado</span>
     </div>
     
     <!-- Header: title + series -->
     <header id="video-header">
       <h1 id="video-title">Nenhum vídeo em reprodução</h1>
       <p id="video-series"></p>
     </header>
     
     <!-- Thumbnail -->
     <div id="thumbnail-container">
       <img id="thumbnail" src="" alt="" />
     </div>
     
     <!-- Progress bar -->
     <div id="progress-container">
       <span id="current-time">00:00</span>
       <input type="range" id="seek-bar" min="0" max="100" value="0" step="0.1" />
       <span id="total-time">00:00</span>
     </div>
     
     <!-- Transport controls -->
     <div id="transport-controls">
       <button id="btn-prev" class="transport-btn" disabled>⏮</button>
       <button id="btn-back" class="transport-btn">⏪</button>
       <button id="btn-play-pause" class="transport-btn primary">▶</button>
       <button id="btn-forward" class="transport-btn">⏩</button>
       <button id="btn-next" class="transport-btn" disabled>⏭</button>
     </div>
     
     <!-- Skip intro button -->
     <button id="btn-skip-intro" class="skip-intro-btn" disabled>
       Pular Abertura (+1:25)
     </button>
     
     <!-- Volume -->
     <div id="volume-container">
       <span id="volume-icon">🔊</span>
       <input type="range" id="volume-bar" min="0" max="100" value="100" />
       <span id="volume-value">100</span>
     </div>
     
     <!-- Device + Profile info -->
     <div id="device-info">
       <span id="device-name"></span>
       <span id="profile-label"></span>
     </div>
     
     <!-- Queue -->
     <div id="queue-section">
       <h3>Próximos</h3>
       <div id="queue-list"></div>
     </div>
     
     <script src="/js/app.js"></script>
   </body>
   </html>
   ```

2. `style.css` — tema escuro, mobile-first:
   - Variáveis CSS: --bg: #121212, --surface: #1E1E1E, --primary: #7C4DFF, --text: #FFFFFF, --text-secondary: #B0B0B0
   - Body: bg --bg, font-family system-ui, color --text
   - Seek bar: custom styled, touch-friendly (height 44px touch area, 4px track)
   - Transport buttons: 56x56px, flex layout, gap
   - Skip intro: destaque com --primary, border-radius, padding generoso
   - Volume: horizontal flex, slider custom
   - Queue: horizontal scroll, cards compactas
   - Connection status: fixed top-right, dot verde/vermelho
   - Responsive: funciona de 320px a 1024px
   - Sem scroll horizontal no body

## Critérios de Aceite
- [ ] Layout responsivo sem scroll horizontal em 320px+
- [ ] Tema escuro consistente (variáveis CSS)
- [ ] Seek bar touch-friendly (44px touch area)
- [ ] Botões 56x56px (adequado para touch)
- [ ] Skip intro com destaque visual
- [ ] Queue compacta
- [ ] Connection indicator fixo
- [ ] Sem scroll horizontal
- [ ] HTML semântico e acessível

## Dependências
- nenhuma (pode ser feito em paralelo com subtask 01-04)

# Subtask 13: Frontend — Router + Home (HTML/CSS/JS)

**Story:** story_04_frontend_home.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Reescrever o frontend SPA para suportar navegação por hash e a tela de home/biblioteca.
Manter o tema escuro e mobile-first existentes. O remote control atual vira a "view" player.

## Arquivos Alvo
- `src/CATRA.Services/WebControl/wwwroot/index.html` (reescrever — adicionar views home + player)
- `src/CATRA.Services/WebControl/wwwroot/css/style.css` (modificar — adicionar estilos home)
- `src/CATRA.Services/WebControl/wwwroot/js/app.js` (reescrever — adicionar router + home logic)

## Passos
1. **HTML**: Estrutura com 3 views (show/hide via JS):
   - `#view-home`: header + "Continue Assistindo" + "Categorias" + "Recentes"
   - `#view-detail`: header + lista de episódios do item
   - `#view-player`: player HTML5 + controles (migrar remote control atual)
2. **CSS**: Estilos para cards de categoria, cards de episódio, grid responsivo, header com back button
3. **JS Router**: 
   - `window.onhashchange` → parse hash → show/hide view
   - `#/` → home, `#/item/{id}` → detail, `#/play/{id}` → player, `#/search?q=` → search
4. **Home Logic**:
   - Fetch `/api/library/categories` → render cards
   - Fetch `/api/library/continue-watching` → render section
   - Click category → navigate to `#/item/{id}`
5. **Detail Logic**:
   - Fetch `/api/library/items/{id}/episodes` → render episode list
   - Click episode → navigate to `#/play/{episodeId}`
6. **Search**: input com debounce 300ms → fetch `/api/library/search?q=` → render results

## Critérios de Aceite
- [ ] Router funciona (hash-based, back/forward do browser)
- [ ] Home exibe categorias e continue assistindo
- [ ] Detail exibe episódios com thumbnails
- [ ] Busca funciona com debounce
- [ ] Mobile-first, sem scroll horizontal
- [ ] Tema escuro mantido
- [ ] Remote control existente migrado para view player (funcional)

## Dependências
- subtask_03 (endpoints REST)
- subtask_10 (WebSocket para updates)

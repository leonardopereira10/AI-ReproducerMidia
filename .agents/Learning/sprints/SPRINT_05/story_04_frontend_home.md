# Story 04 — Frontend: Home/Biblioteca

**Tipo:** dev
**Complexidade:** Média
**Espec:** `.agents/specs/web_panel_streaming_home.md` — Seção 5 (Tela A)

## Descrição

Construir a tela de home/biblioteca no frontend SPA existente. O frontend continua sendo
HTML+CSS+JS puro (sem framework), embutido como recurso no assembly. A home exibe:
"Continue Assistindo", categorias, itens recentes, e busca. Navegação via router hash-based.

## Critérios de Aceite

- [ ] Router hash-based: `#/` (home), `#/item/{id}` (detalhe), `#/play/{id}` (player), `#/search?q=` (busca)
- [ ] Seção "Continue Assistindo" com cards de episódios em progresso
- [ ] Seção "Categorias" com cards navegáveis
- [ ] Tela de detalhe do item: lista de episódios com thumbnail, duração, status
- [ ] Busca com results em tempo real (debounce 300ms)
- [ ] Tema escuro mantido, mobile-first, sem scroll horizontal
- [ ] WebSocket conectado para receber updates de estado
- [ ] Funciona em 320px–1024px

## Dependências

- Story 01 (API de biblioteca)
- Story 03 (WebSocket para updates)

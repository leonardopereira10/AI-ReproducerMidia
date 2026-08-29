# Story 01 — API de Biblioteca (Backend REST)

**Tipo:** dev
**Complexidade:** Média
**Espec:** `.agents/specs/web_panel_streaming_home.md` — Seção 1

## Descrição

Criar endpoints REST no `WebControlServer` para navegar a biblioteca de mídia:
categorias, itens (séries/filmes), episódios, busca e "continue assistindo".

## Critérios de Aceite

- [ ] `GET /api/library/categories` retorna lista de categorias com contagem de itens
- [ ] `GET /api/library/categories/{id}/items` retorna séries/filmes da categoria
- [ ] `GET /api/library/items/{id}/episodes` retorna episódios com perfis disponíveis
- [ ] `GET /api/library/search?q=term` retorna resultados de busca
- [ ] `GET /api/library/continue-watching` retorna episódios em progresso
- [ ] Endpoints retornam JSON com `PropertyNamingPolicy = CamelCase`
- [ ] Testes unitários cobrindo happy path + edge cases

## Dependências

- Nenhuma (base para todas as outras stories)

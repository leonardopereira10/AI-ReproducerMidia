# Story 07 — QA e Testes

**Tipo:** qa
**Complexidade:** Média
**Espec:** `.agents/specs/web_panel_streaming_home.md`

## Descrição

Validação final da sprint: testes de integração, testes de WebSocket, validação manual
end-to-end, e testes cross-browser.

## Critérios de Aceite

- [ ] Testes de integração: API library → StreamService → MediaHttpServer
- [ ] Testes de WebSocket: novos comandos (browse, playEpisode, switchProfile, reportProgress)
- [ ] Build passa sem erros
- [ ] Todos os testes existentes continuam passando (regressão)
- [ ] Validação cross-browser: Chrome desktop, Chrome mobile, Firefox, Edge
- [ ] Todos os critérios de aceite da spec validados

## Dependências

- Todas as stories anteriores

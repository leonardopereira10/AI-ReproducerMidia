# Ordem de Execução — Sprint: Web Control Panel

| Ordem | Subtask | Agente | Complexidade | Dependência |
|-------|---------|--------|-------------|-------------|
| 1 | subtask_01_web_control_core.md | developer-medio | media | nenhuma |
| 2 | subtask_02_web_control_server.md | developer-medio | media | subtask_01 |
| 3 | subtask_05_frontend_html_css.md | developer-medio | media | nenhuma (paralelo com 02) |
| 4 | subtask_03_web_control_service.md | developer-alto | alta | subtask_01 |
| 5 | subtask_04_websocket_handler.md | developer-medio | media | subtask_02, subtask_03 |
| 6 | subtask_06_frontend_js.md | developer-medio | media | subtask_04, subtask_05 |
| 7 | subtask_07_integracao_catra.md | developer-alto | alta | subtask_02, subtask_03, subtask_04 |
| 8 | subtask_08_qa_testes.md | qa-tester-medio | media | subtask_01 a subtask_07 |

## Notas de Execução

- **Lote 1 (paralelo):** subtask_01 é pré-requisito para quase tudo. Executar primeiro.
- **Lote 2 (paralelo):** Após subtask_01, subtask_02 (server) e subtask_05 (HTML/CSS) podem rodar em paralelo.
- **Lote 3:** subtask_03 (service) após subtask_01.
- **Lote 4:** subtask_04 (WebSocket handler) após subtask_02 + subtask_03.
- **Lote 5:** subtask_06 (JS) após subtask_04 + subtask_05.
- **Lote 6:** subtask_07 (integração CATRA) após subtask_02 + subtask_03 + subtask_04.
- **Lote 7:** subtask_08 (QA) após tudo.

## Paralelismo Máximo: 2 agentes simultâneos

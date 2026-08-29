# Ordem de Execução — Sprint 05: Web Panel Streaming Home

| Ordem | Subtask | Story | Agente | Complexidade | Dependência |
|-------|---------|-------|--------|-------------|-------------|
| 1 | subtask_01_library_api_core.md | S01 | developer-baixo | baixa | nenhuma |
| 2 | subtask_05_stream_core.md | S02 | developer-baixo | baixa | nenhuma |
| 3 | subtask_08_extend_models.md | S03 | developer-baixo | baixa | subtask_05 |
| 4 | subtask_02_library_api_service.md | S01 | developer-medio | media | subtask_01 |
| 5 | subtask_06_stream_service_impl.md | S02 | developer-medio | media | subtask_05 |
| 6 | subtask_03_library_endpoints.md | S01 | developer-medio | media | subtask_02 |
| 7 | subtask_07_profiles_endpoint_tests.md | S02 | developer-medio | media | subtask_03, subtask_06 |
| 8 | subtask_04_library_tests.md | S01 | qa-tester-medio | media | subtask_02 |
| 9 | subtask_09_player_client_mgmt.md | S03 | developer-alto | alta | subtask_06, subtask_08 |
| 10 | subtask_10_websocket_routing.md | S03 | developer-alto | alta | subtask_09 |
| 11 | subtask_11_switch_profile.md | S03 | developer-alto | alta | subtask_09, subtask_06 |
| 12 | subtask_12_cast_integration.md | S03 | developer-medio | media | subtask_09, subtask_06 |
| 13 | subtask_13_frontend_home.md | S04 | developer-medio | media | subtask_03, subtask_10 |
| 14 | subtask_14_frontend_player.md | S05 | developer-alto | alta | subtask_13, subtask_11, subtask_12 |
| 15 | subtask_15_di_registration.md | S06 | developer-baixo | baixa | subtask_02, subtask_06, subtask_09 |
| 16 | subtask_16_url_wpf_thumbnails.md | S06 | developer-medio | media | subtask_15 |
| 17 | subtask_17_processed_file_usage.md | S06 | developer-medio | media | subtask_06, subtask_15 |
| 18 | subtask_18_token_lifecycle_faststart.md | S06 | developer-medio | media | subtask_06 |
| 19 | subtask_19_qa_integration_tests.md | S07 | qa-tester-medio | media | subtask_09-12 |
| 20 | subtask_20_qa_final_validation.md | S07 | qa-tester-alto | alta | todas |

## Notas

- **Paralelismo máximo: 2** subagents simultâneos
- Subtasks 1 e 2 podem rodar em paralelo (sem dependência entre si)
- Subtasks 4 e 5 podem rodar em paralelo
- Subtasks 11 e 12 podem rodar em paralelo (ambas dependem de 09+06)
- Subtasks 15-18 são independentes entre si (todas dependem de anteriores)
- QA (19, 20) sempre sequencial após dev

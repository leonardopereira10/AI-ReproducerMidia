# Subtask 19: QA — Testes de Integração + WebSocket

**Story:** story_07_qa_testes.md
**Tipo:** qa
**Complexidade:** media
**Agente:** qa-tester-medio

## Descrição

Testes de integração cobrindo API library, StreamService, e WebSocket commands.

## Arquivos Alvo
- `tests/CATRA.Services.Tests/WebControl/WebControlServiceBrowserTests.cs` (novo)
- `tests/CATRA.Services.Tests/Integration/StreamingIntegrationTests.cs` (novo)

## Passos
1. Testes WebControlService browser mode:
   - playEpisode → state tem mode="browser", streamUrl preenchido
   - reportProgress → WatchState persistido
   - switchProfile → streamUrl muda, posição mantida
   - castTo → mode muda para "dlna"
   - ended → próximo episódio preparado
2. Testes de integração:
   - LibraryApiService → endpoints → JSON response
   - StreamService → ResolveEpisode → MediaHttpServer registration
3. Testes WebSocket handler:
   - browse → resposta "library" na conexão correta
   - relay → comando chega ao player client

## Critérios de Aceite
- [ ] ≥15 testes de integração
- [ ] Build + todos os testes passam
- [ ] Regressão: testes existentes continuam passando

## Dependências
- subtask_09, subtask_10, subtask_11 (backend completo)

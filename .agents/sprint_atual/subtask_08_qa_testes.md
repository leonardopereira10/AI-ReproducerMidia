# Subtask 08: QA — Testes Unitários + Validação Final

**Story:** story_08_qa_testes.md
**Tipo:** qa
**Complexidade:** media
**Agente:** qa-tester-medio

## Descrição
Escrever testes unitários para as camadas críticas e validar a integração end-to-end.

## Arquivos Alvo (fileScope)
- `tests/CATRA.Services.Tests/WebControl/WebControlServiceTests.cs` (NOVO)
- `tests/CATRA.Services.Tests/WebControl/WebSocketHandlerTests.cs` (NOVO)
- `tests/CATRA.Core.Tests/Models/WebControlCommandTests.cs` (NOVO)

## Passos
1. **WebControlCommandTests** (CATRA.Core.Tests):
   - Test FromJson para cada tipo de comando
   - Test FromJson com campos opcionais ausentes
   - Test FromJson com JSON inválido

2. **WebControlServiceTests** (CATRA.Services.Tests):
   - Mock ICastingService, ISlidingWindowService, IEpisodeRepository
   - Test GetCurrentState retorna estado consistente
   - Test HandleCommandAsync("play") chama casting.PlayAsync()
   - Test HandleCommandAsync("pause") chama casting.PauseAsync()
   - Test HandleCommandAsync("seek") chama casting.SeekAsync com posição correta
   - Test HandleCommandAsync("volume") chama casting.SetVolumeAsync
   - Test HandleCommandAsync("skipIntro") calcula target correto
   - Test HandleCommandAsync("nextEpisode") resolve próximo episódio
   - Test SetCurrentEpisode atualiza estado com dados corretos

3. **WebSocketHandlerTests** (CATRA.Services.Tests):
   - Test parse de mensagem JSON válida
   - Test parse de mensagem JSON inválida (não crasha)
   - Test broadcast envia para múltiplos clientes
   - Test conexão fechada é removida da lista

4. **Build validation**:
   - `dotnet build CATRA.sln -c Release`
   - `dotnet test`

## Critérios de Aceite
- [ ] Testes de WebControlCommand (parse JSON)
- [ ] Testes de WebControlService (comandos + estado)
- [ ] Testes de WebSocketHandler (broadcast, cleanup)
- [ ] Build Release sem erros
- [ ] Todos os testes passam

## Dependências
- subtask_01 a subtask_07 (tudo implementado)

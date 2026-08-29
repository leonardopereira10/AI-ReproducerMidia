# Subtask 15: DI Registration + Wire-up

**Story:** story_06_integracao_di.md
**Tipo:** dev
**Complexidade:** baixa
**Agente:** developer-baixo

## Descrição

Registrar todos os novos serviços no DI container e wire-up na inicialização.

## Arquivos Alvo
- `src/CATRA.App/App.xaml.cs` (modificar — DI registration)

## Passos
1. Registrar `ILibraryApiService` → `LibraryApiService` (Singleton)
2. Registrar `IStreamService` → `StreamService` (Singleton)
3. Passar `ILibraryApiService` para `WebControlServer` (via construtor)
4. Passar `IStreamService` para `WebControlService` (via construtor)
5. Passar `IStreamService` para `WebControlServer` (para endpoint de profiles)
6. Verificar que `IProcessedFileUsage` decora com streaming awareness (subtask 17)

## Critérios de Aceite
- [ ] Todos os serviços registrados
- [ ] WebControlServer recebe ILibraryApiService + IStreamService
- [ ] WebControlService recebe IStreamService
- [ ] App inicia sem erros de DI
- [ ] Build passa

## Dependências
- subtask_02, subtask_06, subtask_09 (implementações existentes)

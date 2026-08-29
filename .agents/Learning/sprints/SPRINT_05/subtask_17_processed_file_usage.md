# Subtask 17: ProcessedFileUsage — Proteção de Streaming (D7)

**Story:** story_06_integracao_di.md
**Tipo:** dev
**Complexidade:** media
**Agente:** developer-medio

## Descrição

Estender `ProcessedFileUsage` para incluir sessões de streaming browser, protegendo
arquivos em uso contra deleção pelo `SlidingWindowService`.

## Arquivos Alvo
- `src/CATRA.Services/Processing/ProcessedFileUsage.cs` (modificar)

## Passos
1. Adicionar `IStreamService` como dependência no construtor
2. `IsInUse()`: além de verificar playback/casting, consultar `IStreamService.GetActiveStreamingFiles()`
3. Se o filePath está no set de arquivos em streaming → retornar true
4. Alternativa (decorator): criar `StreamingAwareProcessedFileUsage` que decora o original
   - Registrar no DI: `AddSingleton<IProcessedFileUsage>(sp => new StreamingAwareProcessedFileUsage(new ProcessedFileUsage(...), sp.GetRequiredService<IStreamService>()))`

## Critérios de Aceite
- [ ] Arquivos em streaming browser são reportados como "in use"
- [ ] SlidingWindow não deleta arquivo em streaming
- [ ] CleanupService também respeita (usa mesma interface)
- [ ] Build + testes existentes passam

## Dependências
- subtask_06 (StreamService com GetActiveStreamingFiles)
- subtask_15 (DI registration)

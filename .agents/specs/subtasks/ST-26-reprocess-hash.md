# Subtask 26: Reprocessamento Automático (Hash Check) — STUB

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RN-09, Fase 3)
Depende de ST-18 (SlidingWindowService).

## Objetivo
Detectar quando o arquivo original muda (hash diferente) e re-enfileirar
automaticamente os episódios afetados que estão na janela.

## Escopo (resumido — detalhar na Fase 3)
### Arquivos a Criar
- `src/CATRA.Services/Processing/StaleDetectionService.cs`

### Arquivos a Modificar
- `src/CATRA.Services/Library/LibraryWatcher.cs` — ao detectar mudança → verificar hash
- `src/CATRA.Services/Processing/SlidingWindowService.cs` — re-enfileirar stale

## Requisitos Principais
- Ao FileSystemWatcher detectar Changed em arquivo de vídeo:
  - Recalcular hash (SHA-256)
  - Comparar com `Episode.FileHash`
  - Se diferente: atualizar DB, marcar ProcessedFile como stale
- Se episódio stale está na janela ativa → re-enfileirar automaticamente
- Badge "↻" no EpisodeCardControl (já previsto na ST-19)
- Não reprocessar automaticamente se episódio NÃO está na janela
  (só marca como stale, usuário decide)
- Log: "File changed: {path}, re-queued for processing"

## Critérios de Aceitação
- [ ] Substituir arquivo → hash atualizado no DB
- [ ] Episódio na janela → re-enfileirado automaticamente
- [ ] Episódio fora da janela → badge ↻ (sem auto-reprocessar)
- [ ] Não dispara reprocessamento em massa (só afetados)

## Dependências
- ST-18 (SlidingWindowService)
- ST-03 (LibraryWatcher)

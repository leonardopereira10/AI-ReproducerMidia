# Subtask 25: Busca/Filtro Biblioteca — STUB

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-01, Fase 3)
Depende de ST-04 (HomeView).

## Objetivo
Barra de busca na Home para filtrar séries/filmes por nome. Filtro por
categoria e tipo (série/filme).

## Escopo (resumido — detalhar na Fase 3)
### Arquivos a Criar
- `src/CATRA.UI/Controls/SearchBarControl.xaml` + `.cs`
- `src/CATRA.Services/Library/SearchService.cs`

### Arquivos a Modificar
- `src/CATRA.UI/Views/HomeView.xaml` — adicionar SearchBar na toolbar
- `src/CATRA.UI/ViewModels/HomeViewModel.cs` — filtro de resultados

## Requisitos Principais
- Busca por título (normalizado e raw) — case-insensitive, partial match
- Filtro por tipo: Todos / Séries / Filmes
- Filtro por categoria (combina com tabs existentes)
- Debounce 300ms na digitação
- Highlight do termo nos resultados
- Sem resultados → mensagem "Nenhum resultado para '{termo}'"
- SQLite FTS5 para performance (opcional, LIKE funciona até 500 itens)

## Critérios de Aceitação
- [ ] Busca filtra em tempo real
- [ ] Funciona com nomes normalizados e raw
- [ ] Filtros combinam (busca + categoria + tipo)
- [ ] Performance < 100ms para 500 itens

## Dependências
- ST-04 (HomeView)

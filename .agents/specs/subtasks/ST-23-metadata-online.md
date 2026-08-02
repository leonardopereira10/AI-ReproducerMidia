# Subtask 23: Metadados Online (TMDB/AniList) — STUB

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-09, Fase 3)
Depende de ST-04 (MediaDetailView para exibir metadados).

## Objetivo
Buscar metadados online (sinopse, ano, gênero, poster) em TMDB/AniList.
Match fuzzy pelo nome da pasta. Fallback gracioso se sem match.

## Escopo (resumido — detalhar na Fase 3)
### Arquivos a Criar
- `src/CATRA.Services/Metadata/OnlineMetadataService.cs`
- `src/CATRA.Services/Metadata/TmdbClient.cs`
- `src/CATRA.Services/Metadata/AniListClient.cs`
- `src/CATRA.Services/Metadata/FuzzyMatcher.cs`
- `src/CATRA.Core/Interfaces/IOnlineMetadataService.cs`

## Requisitos Principais
- TMDB API (filmes + séries) + AniList API (animes)
- Match fuzzy: normalizar nome da pasta → comparar com resultados
- Threshold de confiança: se score < 70% → não aplicar (usar metadata local)
- Salvar: TmdbId, Synopsis, Year, Genre, PosterUrl, BackdropUrl
- Download de poster → cache local (`%AppData%/CATRA/posters/`)
- Rate limiting: TMDB 40 req/10s, AniList 90 req/min
- Offline: não bloquear app, skip silencioso

## Critérios de Aceitação
- [ ] Match correto para nomes normalizados ("A Record of a Mortal's Journey")
- [ ] Match correto para abreviações ("ACSADRGT" → ?)
- [ ] Sem match forçado (score baixo → metadata local)
- [ ] Poster baixado e exibido na Detail
- [ ] Funciona offline (sem crash, sem bloqueio)

## Dependências
- ST-04 (UI Detail)

## Notas
- TMDB API key necessária (gratuita, registrar em themoviedb.org)
- AniList: GraphQL API, sem key
- Para animes: AniList tem melhor coverage que TMDB
- Para filmes: TMDB é superior
- Estratégia: tentar AniList primeiro se pasta está em categoria "Animes",
  TMDB caso contrário

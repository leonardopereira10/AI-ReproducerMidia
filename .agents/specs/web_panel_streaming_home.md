# Plano — Web Panel: Home com Streaming + Controle de Perfis

## Visão Geral

Evoluir o painel web atual (remote control DLNA) em uma **home completa** onde o usuário pode:

1. **Navegar a biblioteca** (categorias → séries/filmes → episódios)
2. **Assistir vídeos em streaming** direto no browser (HTML5 `<video>`)
3. **Escolher o perfil de exibição**: Original, Local (processado) ou DLNA (processado)
4. **Controlar a exibição** com o app servidor continuando como host/orquestrador
5. **Manter o controle DLNA** existente (cast para TV) como opção paralela

## Estado Atual

| Componente | Status | O que faz |
|---|---|---|
| `WebControlServer` (Kestrel :5050) | ✅ Pronto | Serve SPA estático + REST `/api/state` + WebSocket `/ws` |
| `WebControlService` | ✅ Pronto | Bridge entre player e web panel (estado + comandos) |
| `WebSocketHandler` | ✅ Pronto | Push de estado/posição em tempo real |
| `MediaHttpServer` (porta efêmera) | ✅ Pronto | Serve arquivos de mídia com byte-range para DLNA |
| Frontend (HTML/CSS/JS) | ✅ Pronto | Remote control mobile-first (transport, seek, volume, queue) |
| `ICastingService` | ✅ Pronto | DLNA: discovery, cast, play/pause/seek/volume |
| `IPlaybackEngine` | ✅ Pronto | Playback local (D3D11VA + audio) |
| `ISlidingWindowService` | ✅ Pronto | Janela de episódios pré-processados |
| `ProcessedFile` | ✅ Pronto | Modelo com perfil (Local/DLNA), caminho, resolução, fps |
| API de biblioteca (REST) | ❌ Inexistente | Nenhum endpoint para listar categorias/episódios |
| Streaming no browser | ❌ Inexistente | Nenhum `<video>` HTML5 servido ao browser |
| Seleção de perfil | ❌ Inexistente | Não há UI para escolher Original/Local/DLNA |

## Arquitetura Proposta

```
┌──────────────────────────────────────────────────────────────────────┐
│                        CATRA.App (WPF — Host)                       │
│                                                                      │
│  LibraryService ──→ ILibraryApiService ←── REST API (/api/library)  │
│       │                    │                                          │
│  PlayerViewModel ──→ IWebControlService ←── WebSocket (/ws)          │
│       │                    │                       │                  │
│       ▼                    ▼                       ▼                  │
│  ICastingService    IStreamService           WebControlServer        │
│  IPlaybackEngine    (register/unregister)    (Kestrel :5050)        │
│  ISlidingWindow     │                          │                     │
│  IProcessedFileRepo │                          │                     │
│                     ▼                          │                     │
│              MediaHttpServer ◄─────────────────┘                     │
│              (byte-range, /media/{token})                            │
└──────────────────────────────┬───────────────────────────────────────┘
                               │ HTTP
                        ┌──────▼──────────────────────┐
                        │     Browser (Celular/PC)     │
                        │                              │
                        │  ┌─────────────────────────┐ │
                        │  │  Home / Biblioteca       │ │
                        │  │  Categorias → Items → Eps│ │
                        │  └─────────────────────────┘ │
                        │  ┌─────────────────────────┐ │
                        │  │  Player (HTML5 <video>)  │ │
                        │  │  Perfil: Orig|Local|DLNA │ │
                        │  │  Controles + DLNA cast   │ │
                        │  └─────────────────────────┘ │
                        └──────────────────────────────┘
```

## Escopo Detalhado

### 1. API de Biblioteca (REST)

Novos endpoints no `WebControlServer`:

```
GET /api/library/categories
    → [{ id, name, folderPath, itemCount }]

GET /api/library/categories/{id}/items
    → [{ id, title, mediaType, year, genre, posterUrl, episodeCount, watchProgress }]

GET /api/library/items/{id}/episodes
    → [{ id, fileName, displayTitle, seasonNumber, episodeNumber,
         durationSec, thumbnailUrl, watched, progressPct,
         availableProfiles: ["original", "local", "dlna"] }]

GET /api/library/episodes/{id}/profiles
    → {
        original: { url: "/media/{token}", width, height, fps, fileSize },
        local:    { url: "/media/{token}", width, height, fps, fileSize } | null,
        dlna:     { url: "/media/{token}", width, height, fps, fileSize } | null
      }

GET /api/library/search?q=term
    → [{ id, title, type: "item"|"episode", ... }]

GET /api/library/continue-watching
    → [{ episodeId, title, seriesTitle, position, duration, thumbnailUrl, ... }]
```

### 2. Serviço de Streaming (`IStreamService`)

Nova interface em `CATRA.Core.Interfaces`:

```csharp
public interface IStreamService
{
    /// <summary>
    /// Registra um arquivo para streaming HTTP e retorna a URL pública.
    /// Usa o MediaHttpServer existente (byte-range support).
    /// </summary>
    string RegisterForStreaming(string filePath, string contentType);

    /// <summary>
    /// Resolve o melhor arquivo para um episódio dado o perfil escolhido.
    /// Perfil "original" → episode.FilePath
    /// Perfil "local" → ProcessedFile com Profile=Local (se existir)
    /// Perfil "dlna"  → ProcessedFile com Profile=Dlna (se existir)
    /// </summary>
    StreamResolution? ResolveEpisode(int episodeId, string profile);

    /// <summary>
    /// Lista perfis disponíveis para um episódio (sempre inclui "original").
    /// </summary>
    List<ProfileInfo> GetAvailableProfiles(int episodeId);

    /// <summary>
    /// Desregistra um token de streaming.
    /// </summary>
    void Unregister(string token);
}

public sealed record StreamResolution(
    string FilePath,
    string ContentType,
    string StreamUrl,
    int Width,
    int Height,
    double Fps,
    long? FileSizeBytes,
    string Profile);

public sealed record ProfileInfo(
    string Name,          // "original", "local", "dlna"
    string Label,         // "Original (720p 24fps)", "Local (1080p 60fps)", "DLNA (4K 55fps)"
    int Width,
    int Height,
    double Fps,
    long? FileSizeBytes,
    bool IsProcessed);
```

Implementação em `CATRA.Services`:
- Injeta `IMediaHttpServer`, `IEpisodeRepository`, `IProcessedFileRepository`
- `RegisterForStreaming` → delega para `MediaHttpServer.RegisterFile()`
- `ResolveEpisode` → busca `ProcessedFile` pelo perfil, faz register e retorna URL
- `GetAvailableProfiles` → sempre retorna "original" + perfis processados existentes

### 3. Modelo de Controle Unificado

O web panel controla **dois modos de exibição**:

| Modo | Host de vídeo | Quem renderiza | Controle |
|---|---|---|---|
| **Browser** | HTML5 `<video>` no browser | Browser (H.264/AAC via MediaSource ou native) | Web panel envia comandos, browser executa |
| **DLNA** | Smart TV / Renderer | DLNA renderer | `ICastingService` (já existe) |

**O app WPF continua sendo o host/orquestrador**:
- Decide qual arquivo servir (original vs processado)
- Registra no `MediaHttpServer`
- Mantém estado de reprodução (posição, fila, watch state)
- Persiste progresso

### 4. WebSocket — Protocolo Estendido

**Novos comandos (cliente → servidor):**

```json
// Navegar biblioteca
{ "type": "browse", "target": "categories" }
{ "type": "browse", "target": "items", "categoryId": 1 }
{ "type": "browse", "target": "episodes", "itemId": 42 }

// Iniciar playback no browser (cliente torna-se player client)
{ "type": "playEpisode", "episodeId": 123, "profile": "local" }

// Controle de player (relay para player client ou DLNA)
{ "type": "play" }
{ "type": "pause" }
{ "type": "seek", "position": 125.5 }
{ "type": "volume", "level": 70 }
{ "type": "skipIntro" }
{ "type": "nextEpisode" }
{ "type": "previousEpisode" }

// Trocar perfil em tempo real (mantém posição, troca stream URL)
{ "type": "switchProfile", "profile": "dlna" }

// Cast para DLNA (a partir do episódio atual)
{ "type": "castTo", "deviceUdn": "uuid:..." }
{ "type": "stopCast" }

// Reporte do player client (browser → servidor)
{ "type": "reportProgress", "position": 125.5 }
{ "type": "ended" }
{ "type": "ready", "duration": 2400.0 }
```

**Novos pushes (servidor → cliente):**

```json
// Estado completo de playback (browser mode)
{
  "type": "state",
  "data": {
    "mode": "browser",           // "browser" | "dlna" | "idle"
    "isPlaying": true,
    "isPaused": false,
    "position": 125.5,
    "duration": 2400.0,
    "volume": 70,
    "title": "Episódio 1",
    "seriesTitle": "Anime Name",
    "thumbnailUrl": "/api/thumbnail/123",
    "streamUrl": "http://192.168.1.10:8181/media/abc123",  // URL completa do stream
    "activeProfile": "local",
    "availableProfiles": [
      { "name": "original", "label": "Original (720p 24fps)", "width": 1280, "height": 720 },
      { "name": "local", "label": "Local (1080p 60fps)", "width": 1920, "height": 1080 },
      { "name": "dlna", "label": "DLNA (4K 55fps)", "width": 3840, "height": 2160 }
    ],
    "skipIntroSec": 85,
    "canSkipIntro": true,
    "hasNextEpisode": true,
    "hasPreviousEpisode": false,
    "castDeviceName": null,
    "availableDevices": [],
    "isPlayerClient": true,     // se este cliente é o player client
    "queue": [...]
  }
}

// Comando relay (servidor → player client específico)
{
  "type": "command",
  "command": { "type": "seek", "position": 125.5 }
}

// Lista de biblioteca (quando browse é solicitado)
{
  "type": "library",
  "target": "categories",
  "items": [...]
}

// Dispositivos DLNA encontrados
{
  "type": "devices",
  "items": [{ "udn": "...", "name": "Samsung TV", "manufacturer": "Samsung" }]
}
```

### 5. Frontend — Nova Home

O frontend atual é apenas um remote control. A nova home terá **duas telas principais**:

#### Tela A — Biblioteca (Home)
```
┌─────────────────────────────────────────┐
│  CATRA                          🔍  ⚙️  │
├─────────────────────────────────────────┤
│                                         │
│  ▶ Continuar Assistindo                 │
│  ┌──────┐ ┌──────┐ ┌──────┐            │
│  │ thumb │ │ thumb │ │ thumb │           │
│  │ Ep 5  │ │ Ep 12 │ │ Ep 3  │           │
│  │ 1:23:4│ │ 0:45:2│ │ 2:10:0│           │
│  └──────┘ └──────┘ └──────┘            │
│                                         │
│  📂 Categorias                          │
│  ┌──────┐ ┌──────┐ ┌──────┐            │
│  │Animes│ │Filmes│ │Séries│             │
│  │  42  │ │  15  │ │  28  │             │
│  └──────┘ └──────┘ └──────┘            │
│                                         │
│  🕐 Adicionados Recentemente            │
│  ┌──────┐ ┌──────┐ ┌──────┐            │
│  │ thumb │ │ thumb │ │ thumb │           │
│  └──────┘ └──────┘ └──────┘            │
│                                         │
└─────────────────────────────────────────┘
```

#### Tela B — Player
```
┌─────────────────────────────────────────┐
│  ← Voltar              Perfil: [Local▼] │
├─────────────────────────────────────────┤
│                                         │
│  ┌─────────────────────────────────────┐│
│  │                                     ││
│  │         HTML5 <video>               ││
│  │         (stream HTTP)               ││
│  │                                     ││
│  └─────────────────────────────────────┘│
│                                         │
│  ████████████░░░░░░░░░░  12:34 / 24:00 │
│                                         │
│  ⏮  ⏪  ▶/⏸  ⏩  ⏭                    │
│  [Pular Abertura]                       │
│                                         │
│  🔊 ━━━━━━━━━○━━  70                   │
│                                         │
│  ─────────────────────────────────────  │
│  Perfis:  ○ Original  ● Local  ○ DLNA  │
│                                         │
│  📺 Cast: [Samsung TV ▼]  [Cast] [Stop]│
│                                         │
│  Próximos:                              │
│  ┌──────┐ ┌──────┐ ┌──────┐            │
│  │ Ep 4  │ │ Ep 5  │ │ Ep 6  │           │
│  └──────┘ └──────┘ └──────┘            │
└─────────────────────────────────────────┘
```

### 6. Navegação Frontend (SPA sem framework)

Router simples baseado em hash:
- `#/` → Home (biblioteca)
- `#/item/{id}` → Detalhe da série/filme (lista episódios)
- `#/play/{episodeId}` → Player com perfil padrão
- `#/play/{episodeId}?profile=dlna` → Player com perfil específico
- `#/search?q=term` → Busca

### 6.1 Roteamento de estáticos no WebControlServer

O `WebControlServer.HandleRequestAsync` atual é um switch hardcoded para 3 assets. A nova home exige:
- Suporte a múltiplos arquivos estáticos (novos CSS/JS para home e player)
- Ou consolidar tudo em poucos arquivos (index.html + style.css + app.js permanecem, mas com conteúdo expandido)
- **Decisão**: Manter os 3 arquivos existentes mas expandir seu conteúdo. A home e o player são "views" dentro do mesmo SPA (show/hide via JS router).

### 7. Integração com MediaHttpServer

O `MediaHttpServer` já suporta byte-range — perfeito para streaming HTML5:
- O browser envia `Range: bytes=0-` → servidor responde `206 Partial Content`
- Seek no `<video>` → browser envia novo `Range` → servidor responde com chunk
- **Reutilização total** — zero mudanças no `MediaHttpServer`

Fluxo:
1. Usuário clica "Assistir" em um episódio
2. Frontend envia `{ "type": "playEpisode", "episodeId": 123, "profile": "local" }`
3. `WebControlService` → `IStreamService.ResolveEpisode(123, "local")`
4. `IStreamService` busca `ProcessedFile` com Profile=Local, registra no `MediaHttpServer`
5. Responde com `streamUrl` (ex: `http://192.168.1.10:8181/media/abc123`)
6. Frontend recebe `{ "type": "state", "data": { "streamUrl": "...", ... } }`
7. Frontend seta `video.src = streamUrl` e dá play

## Stories e Subtasks

### Story 1 — API de Biblioteca (Backend REST)
**Complexidade: Média** | **Estimativa: 1 dia**

| # | Subtask | Complexidade |
|---|---|---|
| 1.1 | Criar `ILibraryApiService` em Core (interfaces + DTOs) | Baixa |
| 1.2 | Implementar `LibraryApiService` em Services (categorias, items, episódios) | Média |
| 1.3 | Adicionar endpoints REST no `WebControlServer` (`/api/library/*`) | Média |
| 1.4 | Adicionar busca (`/api/library/search`) e continue-watching | Média |
| 1.5 | Testes unitários do `LibraryApiService` | Média |

### Story 2 — Serviço de Streaming
**Complexidade: Média** | **Estimativa: 1 dia**

| # | Subtask | Complexidade |
|---|---|---|
| 2.1 | Criar `IStreamService` + `StreamResolution` + `ProfileInfo` em Core | Baixa |
| 2.2 | Implementar `StreamService` em Services (resolve perfil, register no MediaHttpServer) | Média |
| 2.3 | Endpoint `/api/library/episodes/{id}/profiles` (REST) | Baixa |
| 2.4 | Integração com `WebControlService` (playEpisode, switchProfile) | Média |
| 2.5 | Testes unitários do `StreamService` | Média |

### Story 3 — WebSocket Estendido + Browser Mode Control Loop
**Complexidade: Alta** | **Estimativa: 2 dias**

| # | Subtask | Complexidade |
|---|---|---|
| 3.1 | Estender `WebControlCommand` com novos tipos (browse, playEpisode, switchProfile, castTo, reportProgress, ended, ready) | Baixa |
| 3.2 | Estender `WebControlState` com mode, streamUrl, availableProfiles, availableDevices, isPlayerClient | Baixa |
| 3.3 | Player client management no `WebControlService` (election, tracking, command relay) | Alta |
| 3.4 | Atualizar `WebSocketHandler` para push de library/devices/command messages + routing por client type | Alta |
| 3.5 | Reporte de posição browser→server (`reportProgress`, `ended`) + persistência `WatchState` | Alta |
| 3.6 | Comando `switchProfile` (troca stream mantendo posição, tolerância ±2s) | Alta |
| 3.7 | Integração com `ICastingService` para castTo/stopCast via WebSocket | Média |

### Story 4 — Frontend: Home/Biblioteca
**Complexidade: Média** | **Estimativa: 1.5 dias**

| # | Subtask | Complexidade |
|---|---|---|
| 4.1 | Nova estrutura HTML (home + player + navegação SPA) | Média |
| 4.2 | CSS para home (cards, categorias, continue-watching) | Média |
| 4.3 | JS: Router hash-based + fetch API (biblioteca) | Média |
| 4.4 | JS: Render de categorias, items, episódios, busca | Média |
| 4.5 | JS: Continue-watching section | Baixa |

### Story 5 — Frontend: Player com Streaming
**Complexidade: Alta** | **Estimativa: 2 dias**

| # | Subtask | Complexidade |
|---|---|---|
| 5.1 | HTML5 `<video>` element + integração com stream URL | Alta |
| 5.2 | Controles de player (play/pause/seek/volume) via HTML5 | Média |
| 5.3 | Seletor de perfil (Original/Local/DLNA) com info de resolução | Média |
| 5.4 | Comando `switchProfile` (mantém posição, troca stream) | Alta |
| 5.5 | Sincronização posição server↔browser (polling ou TimeRanges) | Alta |
| 5.6 | Seção de DLNA cast (descobrir dispositivos, cast, stop) | Média |
| 5.7 | Fila de próximos + auto-play next episode | Média |

### Story 6 — Integração, DI e Proteção de Arquivos
**Complexidade: Média** | **Estimativa: 1 dia**

| # | Subtask | Complexidade |
|---|---|---|
| 6.1 | Registrar novos serviços no DI container (`IStreamService`, `ILibraryApiService`) | Baixa |
| 6.2 | Wire-up no `App.xaml.cs` / `MainWindow` (inicialização) | Baixa |
| 6.3 | Exibir URL do painel web no app WPF (status bar, porta de `AppSettingsModel.WebPanelPort`) | Baixa |
| 6.4 | Thumbnails/posters via API (servir imagens pelo WebControlServer, corrigir `ResolveThumbnailUrl` que retorna path cru) | Média |
| 6.5 | Estender `ProcessedFileUsage` para incluir sessões de streaming browser (decorator/composição com `IStreamService`) | Média |
| 6.6 | Token lifecycle: unregister on episode change, session end, TTL cleanup | Média |
| 6.7 | Garantir `-movflags +faststart` no muxer MP4 do `AudioMuxer` (D9) — moov atom no início para start/seek imediato no browser | Baixa |

### Story 7 — QA e Testes
**Complexidade: Média** | **Estimativa: 1 dia**

| # | Subtask | Complexidade |
|---|---|---|
| 7.1 | Testes de integração (API library → StreamService → MediaHttpServer) | Média |
| 7.2 | Testes do WebSocket (novos comandos) | Média |
| 7.3 | Teste manual end-to-end (browser → API → streaming) | Média |
| 7.4 | Validação cross-browser (Chrome, Firefox, Edge mobile) | Baixa |

## Ordem de Execução

```
Sprint Day 1:
  ├── 1.1 → 1.2 → 1.3 (API Biblioteca base)
  ├── 2.1 → 2.2 (StreamService base)
  └── 3.1 → 3.2 (Models estendidos)

Sprint Day 2:
  ├── 1.4 → 1.5 (API completa + testes)
  ├── 2.3 → 2.4 → 2.5 (Streaming completo + testes)
  └── 3.3 (WebControlService browser mode)

Sprint Day 3:
  ├── 3.4 → 3.5 → 3.6 (WebSocket completo)
  ├── 6.1 → 6.2 (DI + wire-up)
  └── 4.1 → 4.2 (Frontend home base)

Sprint Day 4:
  ├── 4.3 → 4.4 → 4.5 (Frontend home completo)
  └── 5.1 → 5.2 (Player HTML5 base)

Sprint Day 5:
  ├── 5.3 → 5.4 (Perfil + switch)
  ├── 5.5 → 5.6 (Sync + DLNA cast)
  └── 5.7 (Queue + auto-play)

Sprint Day 6:
  ├── 6.3 → 6.4 → 6.5 → 6.6 (Integração final + proteção de arquivos + token lifecycle)
  ├── 6.7 (faststart no AudioMuxer, D9)
  └── 7.1 → 7.2 → 7.3 → 7.4 (QA)
```

## Decisões Arquiteturais

### D1: Streaming via HTTP byte-range (não WebSocket binary)
**Decisão**: Usar HTTP com byte-range para streaming de vídeo (HTML5 `<video>` nativo).
**Racional**: O `MediaHttpServer` já suporta byte-range. HTML5 `<video>` faz seek automaticamente via Range headers. Não precisa de MediaSource Extensions ou WebSocket binary. Simples, robusto, compatível com todos os browsers.

### D2: App WPF continua como host
**Decisão**: O app WPF mantém o estado global, registra arquivos, orquestra perfis.
**Racional**: O usuário pediu explicitamente "o host continuará sendo o app servidor". O browser é apenas um renderer remoto.

### D3: Perfis como seleção de arquivo, não transcode em tempo real
**Decisão**: Trocar perfil = trocar o arquivo servido (original vs processado pré-existente).
**Racional**: Transcodificação em tempo real seria extremamente complexo e custoso. Os perfis Local/DLNA já são pré-processados pelo pipeline. A troca mantém a posição (seek para o mesmo timestamp no novo arquivo).

### D4: Browser mode e DLNA mode são mutuamente exclusivos
**Decisão**: Ou está tocando no browser, ou está fazendo cast para DLNA. Não ambos simultaneamente.
**Racional**: Evita complexidade de sincronizar dois renderers. O usuário escolhe o modo.

### D6: Browser mode — protocolo de controle bidirecional (Bloqueante PO)
**Decisão**: O browser que está exibindo o `<video>` é o "player client". Outros dispositivos são "observers/remote controls".
**Protocolo:**
- **Player client → Servidor**: `reportProgress` (posição atual via `timeupdate`, ~1s), `ended` (quando vídeo termina), `ready` (quando metadata carregada)
- **Servidor → Player client**: `command` (relay de comandos recebidos de outros clientes: play, pause, seek, volume, switchProfile)
- **Servidor → Todos**: `state` (estado completo atualizado), `position` (posição consolidada)
- **Eleição**: O primeiro cliente que envia `playEpisode` torna-se o player client. Se desconecta, o próximo que solicitar playback assume. Apenas UM player client por vez.
- **WatchState**: Servidor persiste progresso baseado nos `reportProgress` recebidos do player client (não de polling).

### D7: Proteção de arquivo em streaming (Bloqueante PO)
**Decisão**: `IStreamService` mantém um set de tokens ativos com filePath. `ProcessedFileUsage.IsInUse()` consulta esse set além de playback/casting.
**Implementação**: `StreamService` implementa `IProcessedFileUsage` (decorator ou composição) — quando `RegisterForStreaming` é chamado, o filePath é adicionado ao set; quando `Unregister` ou sessão termina, é removido.

### D8: Content-type mapping
**Decisão**: Mapeamento extensão→MIME em `StreamService`: `.mp4`→`video/mp4`, `.mkv`→`video/x-matroska`, `.webm`→`video/webm`, `.ts`→`video/mp2t`. Fallback: `application/octet-stream`.

### D9: movflags +faststart
**Nota**: O pipeline de processamento deve garantir `movflags +faststart` no muxer FFmpeg para que o moov atom fique no início do MP4, permitindo start/seek imediato via byte-range. Verificar no `AudioMuxer`.

### D5: Frontend sem framework (HTML + CSS + JS puro)
**Decisão**: Manter a abordagem atual sem React/Vue/Angular.
**Racional**: O projeto já usa essa abordagem. Evita build tooling, mantém simplicidade de deploy (recursos embutidos).

## Riscos

| Risco | Impacto | Mitigação |
|---|---|---|
| Browser não suporta codec do arquivo processado (HEVC) | Alto | Fallback para original (H.264); detectar via `canPlayType()` no JS |
| Seek impreciso entre perfis (posição não exata) | Médio | Keyframe seek + tolerância de ±2s (critério de aceite formalizado) |
| Múltiplos clients com perfis diferentes | Médio | Estado global (último comando wins), perfis são por-sessão |
| `MediaHttpServer` porta efêmera não acessível pelo browser | Alto | Expor URL com IP LAN correto (já faz via `GetLocalIp()`) |
| Arquivo em streaming deletado pelo SlidingWindow | Alto | `ProcessedFileUsage` estendido cobre sessões de streaming (D7) |
| Safari/iOS: MKV não suportado, autoplay bloqueado | Médio | Fallback para MP4 original; autoplay exige gesto do usuário (documentar) |
| movov atom no fim do MP4 atrasa start/seek | Médio | Verificar `movflags +faststart` no pipeline (D9) |
| Smart TV não suporta HEVC via browser | Baixo | Não é caso de uso — browser é celular/PC |

## Critérios de Aceite

- [ ] Browser acessa `http://<ip>:5050` e vê a home com categorias
- [ ] Navega até um episódio e vê perfis disponíveis (Original/Local/DLNA)
- [ ] Clica "Assistir" e o vídeo toca no browser via HTML5 `<video>`
- [ ] Troca de perfil mantém a posição de playback
- [ ] Controles funcionam: play/pause, seek, volume, skip intro, next/prev
- [ ] Pode cast para DLNA a partir do web panel (mantém episódio/perfil)
- [ ] "Continue assistindo" mostra episódios em progresso
- [ ] Busca funciona (nome da série, episódio)
- [ ] App WPF: reprodução local desktop e cast DLNA permanecem funcionais e independentes
- [ ] URL do painel visível no app desktop (porta configurável via `AppSettingsModel.WebPanelPort`)
- [ ] Mobile-first, sem scroll horizontal, tema escuro
- [ ] Fallback HEVC: browser detecta via `canPlayType()`, fallback para original H.264 automaticamente
- [ ] Regressão DLNA: controle existente (play/pause/seek/volume via DLNA) continua funcionando sem alterações
- [ ] Troca de perfil mantém posição com tolerância de ±2s
- [ ] Arquivo em streaming não é deletado pelo SlidingWindow durante reprodução
- [ ] WatchState persistido corretamente no modo browser (via `reportProgress`)

# Plano de Execução — Web Control Panel (Painel de Controle Web)

## Objetivo

Construir um painel web mobile-first que permite controlar remotamente a reprodução de vídeo do CATRA via DLNA, acessível por qualquer navegador na rede local. O usuário inicia a transmissão no PC e controla pelo celular.

## Contexto Arquitetural

- **CATRA** é um app WPF desktop com transmissão DLNA via `ICastingService`
- `ICastingService` já expõe: Play, Pause, Stop, Seek, SetVolume, GetPosition, StateChanged, PositionChanged, MediaEnded
- `IMediaHttpServer` usa Kestrel (ASP.NET Core) para servir arquivos de mídia — roda em porta separada
- `PlayerViewModel` contém toda a lógica de playback (skip intro, next episode, queue, etc.)
- `ISlidingWindowService` gerencia a janela de episódios pré-processados
- **Não existe** nenhuma API HTTP/WebSocket de controle remoto hoje

## Escopo

### Incluído (MVP)
- Servidor HTTP embutido no CATRA (Kestrel) com API REST + WebSocket
- Exibição do vídeo em reprodução (título, thumbnail, posição/duração, perfil)
- Controles: Play/Pause, Seek (barra de progresso), Volume, Pular Abertura, Próximo Episódio
- Visualização da fila de próximos episódios (janela deslizante, sem reordenar)
- Sincronização em tempo real via WebSocket (estado + posição)
- Frontend mobile-first, tema escuro, HTML+CSS+JS puro (sem framework)
- Acesso sem autenticação (rede local)
- Controle único por vez (um cliente controla, outros apenas observam)

### Fora de Escopo
- Autenticação/autorização
- Controle multi-dispositivo simultâneo
- Acesso remoto (internet)
- Reordenação da fila
- Upload/gerenciamento de biblioteca
- Suporte a múltiplos rooms/zonas

## Arquitetura Proposta

```
┌─────────────────────────────────────────────────────────────┐
│                    CATRA.App (WPF)                          │
│                                                             │
│  PlayerViewModel ──→ IWebControlService ←── IWebControlHub │
│       │                    │                       │        │
│       ▼                    ▼                       ▼        │
│  ICastingService    Estado centralizado      WebSocket      │
│  ISlidingWindow     (posição, fila, info)    Server         │
│  IWatchState                                    │           │
│                                              HTTP :5050     │
└─────────────────────────────────────────────────┬───────────┘
                                                  │
                                          ┌───────▼───────┐
                                          │  Celular      │
                                          │  Browser      │
                                          │  (SPA estático)│
                                          └───────────────┘
```

### Camadas

1. **`IWebControlHub`** (CATRA.Core) — Interface do hub de controle: publica eventos de estado para clientes conectados
2. **`IWebControlService`** (CATRA.Core) — Interface do serviço: expõe estado atual + comandos
3. **`WebControlService`** (CATRA.Services) — Implementação: orquestra ICastingService, ISlidingWindowService, IWatchStateService
4. **`WebControlServer`** (CATRA.Services) — Kestrel standalone na porta 5050:
   - `GET /` → serve o SPA estático (HTML+CSS+JS embutidos como recursos)
   - `GET /api/state` → snapshot do estado atual (REST)
   - `WS /ws` → WebSocket bidirecional (comandos + push de estado)
5. **Frontend** — Arquivos estáticos (HTML, CSS, JS) embutidos como recursos no assembly

### Protocolo WebSocket

**Cliente → Servidor (comandos):**
```json
{ "type": "play" }
{ "type": "pause" }
{ "type": "seek", "position": 125.5 }
{ "type": "volume", "level": 70 }
{ "type": "skipIntro" }
{ "type": "nextEpisode" }
{ "type": "previousEpisode" }
```

**Servidor → Cliente (estado):**
```json
{
  "type": "state",
  "data": {
    "isPlaying": true,
    "isPaused": false,
    "position": 125.5,
    "duration": 2400.0,
    "volume": 70,
    "title": "Episódio 1 - Pilot",
    "thumbnailUrl": "/api/thumbnail/42",
    "skipIntroSec": 85,
    "canSkipIntro": true,
    "hasNextEpisode": true,
    "hasPreviousEpisode": false,
    "castDeviceName": "Samsung TV",
    "profileLabel": "📺 4K 55fps",
    "queue": [
      { "id": 2, "title": "Ep 2 - The Return", "duration": 2380 },
      { "id": 3, "title": "Ep 3 - The Chase", "duration": 2410 }
    ]
  }
}
```

**Push de posição (a cada ~1s quando playing):**
```json
{ "type": "position", "position": 126.5, "duration": 2400.0 }
```

### Frontend (Mobile-First, Dark Theme)

- **Tela principal**: Info do vídeo atual + controles
  - Header: título do episódio + nome da série
  - Thumbnail (se disponível)
  - Barra de progresso (seek) com tempo atual/total
  - Botões: ⏮ | ⏪ | ▶/⏸ | ⏩ | ⏭
  - Botão "Pular Abertura" (destaque visual)
  - Slider de volume
  - Label do dispositivo + perfil
- **Seção de fila**: próximos episódios (scroll horizontal ou lista compacta)
- **Tema**: fundo escuro (#121212), acentos em azul/roxo, texto branco
- **Responsivo**: funciona em 320px–1024px

## Critérios de Aceite

- [ ] Celular na mesma rede acessa `http://<ip-pc>:5050` e vê o painel
- [ ] Painel exibe informações do vídeo em reprodução (título, posição, duração, thumbnail)
- [ ] Play/Pause funciona (com feedback visual no painel)
- [ ] Seek via barra de progresso funciona (arrastar e soltar)
- [ ] Volume controla o volume da TV (DLNA)
- [ ] "Pular Abertura" avança o tempo configurado (mesma lógica do PlayerViewModel)
- [ ] Próximo/Anterior episódio funciona (navega e retoma transmissão automaticamente)
- [ ] Fila de próximos episódios é exibida (da janela deslizante)
- [ ] Estado sincroniza em tempo real (posição atualiza a cada ~1s)
- [ ] Tema escuro, mobile-first, sem scroll horizontal
- [ ] Servidor inicia automaticamente com o CATRA (ou sob demanda via toggle)
- [ ] URL de acesso exibida na interface do CATRA (ex: barra de status)

## Riscos

| Risco | Mitigação |
|-------|-----------|
| Porta 5050 bloqueada pelo firewall | `FirewallHelper` já existe — reutilizar para abrir a porta |
| Kestrel conflita com MediaHttpServer | Usar porta diferente (5050 vs porta efêmera do media) |
| WebSocket não suportado em Smart TV browsers | Não é problema — painel é para celular/PC, não para a TV |
| Estado dessincronizado (cliente desconecta/reconecta) | REST `/api/state` fornece snapshot completo na conexão |
| Múltiplos clientes conectados | broadcast do estado para todos; último comando recebido wins |

## Dependências Técnicas

- ASP.NET Core (já disponível via Kestrel no MediaHttpServer)
- `System.Net.WebSockets` (built-in no ASP.NET Core)
- Recursos embutidos (`.resx` / `EmbeddedResource`) para servir HTML/CSS/JS
- `ICastingService`, `ISlidingWindowService`, `IWatchStateService`, `IThumbnailService` (todos já existem)

## Estimativa

- **8-10 subtasks** de complexidade média
- **~3-4 dias** de trabalho para um developer-medio
- Frontend é simples (sem framework), maior esforço está na camada de serviço e WebSocket

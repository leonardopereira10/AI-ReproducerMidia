# CATRA — Reprodutor de Mídia Desktop

## Visão Geral

App desktop Windows para reprodução e transmissão de mídia local, organizado em
estrutura de pastas `Categoria > Série/Filme > Episódios`.

**Diferencial central:** pipeline de pre-processamento GPU offline que aplica
frame interpolation + upscale FSR 4 (RDNA 4) antes da reprodução/transmissão.
O conteúdo é processado uma vez e armazenado — playback e DLNA servem o arquivo
já processado, eliminando complexidade em tempo real.

**Nome do projeto:** CATRA (placeholder)
**Repositório:** `C:\Projetos\Reprodutor_CATRA`
**Hardware alvo:** AMD RX 9070 XT (RDNA 4, aceleradores ML dedicados)
**Conteúdo típico:** anime 720p/1080p 24fps

---

## Stack Tecnológica

| Camada | Tecnologia | Justificativa |
|---|---|---|
| **UI Framework** | **WPF (.NET 8+)** | Maduro, custom controls, acesso WinRT (Bluetooth), theme do Windows |
| **Video Decode** | **FFmpeg** (FFmpeg.AutoGen) | D3D11VA hw decode, MKV/MP4/AVI, múltiplos áudios/legendas |
| **Frame Interp** | **RIFE v4** (ONNX/DirectML) ou **FSR 3 FG SDK** | Interpolação ML 24fps→135/55fps; offline, qualidade > tempo |
| **GPU Upscale** | **FSR 4 SDK** (FidelityFX, DX12 Compute) | ML upscale RDNA 4, 720p/1080p→4K agressivo |
| **GPU Encode** | **AMF** (AMD Media Framework) | H.265 hw encode, zero CPU |
| **Render Local** | **DX11/DX12 SwapChain** via HwndHost | Embed em WPF |
| **DLNA/Casting** | **UPnP AVTransport** (Rssdp + SOAP) | Discovery SSDP + controle DLNA p/ Samsung Smart TV |
| **HTTP Server** | **Kestrel embedded** | Serve arquivos processados com Range requests |
| **Database** | **SQLite** (sqlite-net-pcl, WAL) | Leve, local, zero-config |
| **Native Bridge** | **C++ DLL** (C ABI, P/Invoke) | FSR 4 + RIFE/FSR3 FG + AMF são C++/DX12 |
| **Bluetooth** | **Windows.Devices.Bluetooth** (WinRT) | Futuro: controle remoto via celular |
| **DI** | **Microsoft.Extensions.DependencyInjection** | Padrão .NET |
| **MVVM** | **CommunityToolkit.Mvvm** | Source generators, leve |
| **Theming** | **WPF SystemTheme** + ResourceDictionary | Segue light/dark do Windows |

### Arquitetura: Pre-Processamento Offline

A decisão central é separar **processamento pesado** (offline) de **reprodução**
(tempo real simples):

```
┌─────────────────────────────────────────────────────────────────┐
│              PRE-PROCESSAMENTO (offline, batch)                 │
│                                                                 │
│  Arquivo original (.mp4/.avi/.mkv, 720p/1080p, 24fps)          │
│      │                                                          │
│      ▼                                                          │
│  FFmpeg Decode (D3D11VA, hw accel)                              │
│      │  output: frames GPU (ID3D11Texture2D)                    │
│      ▼                                                          │
│  Frame Interpolation (RIFE/FSR3 FG, DirectML/DX12)             │
│      │  24fps → 135fps (local) ou 24fps → 55fps (DLNA)        │
│      ▼                                                          │
│  FSR 4 Upscale (DX12 Compute, RDNA 4 ML)                       │
│      │  720p/1080p → 1080p (local) ou → 4K (DLNA)             │
│      │  (skip upscale se source já é ≥ target)                  │
│      ▼                                                          │
│  AMF H.265 Encode (hw encoder)                                  │
│      │  bitrate configurável por perfil                         │
│      ▼                                                          │
│  Arquivo processado (.mp4 H.265)                                │
│      → cache/processed/{episodeId}_{profile}.mp4                │
│                                                                 │
│  ⏱ Tempo esperado: minutos por episódio (aceitável)            │
│  🖥 Carga GPU: alta durante processamento (aceitável)           │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│              REPRODUÇÃO (tempo real, leve)                      │
│                                                                 │
│  [LOCAL]                                                        │
│  Arquivo processado (1080p 135fps H.265)                        │
│      → FFmpeg Decode (D3D11VA) → DX11 SwapChain → WPF          │
│      → Audio: WASAPI                                            │
│  (decode hw de H.265 1080p é trivial pra RDNA 4)               │
│                                                                 │
│  [DLNA]                                                         │
│  Arquivo processado (4K 55fps H.265)                            │
│      → Kestrel HTTP (Range requests) → AVTransport → TV         │
│      → TV decodifica H.265 4K nativamente                       │
│  (zero GPU no PC durante transmissão — só HTTP serve)           │
└─────────────────────────────────────────────────────────────────┘
```

**Vantagens do modelo offline:**
- Playback local: decode H.265 1080p é trivial, zero upscale em tempo real
- DLNA: file serving puro, sem transcode real-time, sem latência de pipeline
- Seek: instantâneo (Range request no arquivo processado)
- GPU livre durante reprodução (só decode hw leve)
- Qualidade máxima: processamento sem pressão de tempo

### Perfis de Processamento

| Perfil | Target FPS | Target Res | Upscale | Uso |
|---|---|---|---|---|
| **local** | 135 fps | 1080p | FSR 4 se source < 1080p | Reprodução no PC (monitor 135Hz) |
| **dlna** | 55 fps | 4K (2160p) | FSR 4 sempre | Transmissão Samsung TV |

- FPS e resolução configuráveis por perfil em Settings
- Skip intro aplicado no arquivo processado? **Não** — skip é controle de playback
- Se source já é ≥ target resolution → skip upscale (passthrough)
- Se source já é ≥ target FPS → skip interpolation (passthrough)

### Frame Interpolation — Tecnologia

| Opção | API | Qualidade | Nota |
|---|---|---|---|
| **RIFE v4.x** | ONNX Runtime + DirectML | ★★★★★ | Open source, feito pra vídeo, arbitrary timestep |
| **FSR 3 FG SDK** | DX12 Compute | ★★★★ | Feito pra jogos, adaptável pra vídeo offline |
| **Fallback: blend** | CPU/GPU simples | ★★ | Duplicação/blend se ML indisponível |

**Estratégia:** RIFE como primário (melhor qualidade pra vídeo), FSR 3 FG como
alternativa. Ambos rodam em RDNA 4 via DirectML/DX12. Como é offline, qualidade
> velocidade.

Para 24fps → 135fps (5.625x): RIFE arbitrary timestep interpola diretamente.
Para 24fps → 55fps (2.29x): idem.

### FSR 4 — Estado e Estratégia

| Aspecto | Detalhe |
|---|---|
| **SDK** | FidelityFX FSR 4 (GPUOpen), DX12 Compute shaders |
| **Hardware** | Requer RDNA 4 (RX 9070 XT ✅) — aceleradores ML dedicados |
| **API** | DX12 only (não Vulkan) |
| **Driver upgrade (3.1→4)** | ❌ Não aplicável — só funciona para jogos com FSR 3.1 FG |
| **Integração** | C++ DLL nativa → P/Invoke do C# |
| **Fallback** | FSR 1 (espacial, qualquer GPU) se FSR 4 SDK indisponível |

---

## Requisitos Funcionais

### RF-01: Biblioteca de Mídia

- [ ] Escanear pasta raiz ao abrir o app
- [ ] Botão manual "Atualizar Biblioteca"
- [ ] Pasta raiz configurável (default: pasta do executável)
- [ ] Estrutura esperada: `Raiz/Categoria/Série ou Filme/arquivos`
- [ ] Formatos suportados: `.mp4`, `.avi`, `.mkv`
- [ ] Distinguir série (múltiplos arquivos) de filme (arquivo único) automaticamente
- [ ] FileSystemWatcher para detectar mudanças (novos arquivos, renomeação, deleção)

### RF-02: Parser de Nomes de Arquivo

Padrões identificados a partir das fontes reais do usuário:

| # | Padrão | Exemplo | Extração |
|---|---|---|---|
| P1 | `[Site][Nome] - Episódio NN.ext` | `[AniDong][A Record of a Mortal_s Journey] - Episódio 26.mp4` | nome=`A Record of a Mortal's Journey`, ep=26 |
| P2 | `[Site] Nome - Episódio NN (Qualidade).ext` | `[AnimeFire.io] Saikyou Degarashi... - Episódio 4 (HD).mp4` | nome=`Saikyou Degarashi...`, ep=4 |
| P3 | `ABREV##EP##.ext` | `ACSADRGT01EP07.mp4` | temporada=01, ep=07 |
| P4 | `ABREV.ext` (filme) | `SUACLPLNDRS.mp4` | sem metadata útil → usar metadado do arquivo ou nome da pasta |

**Regras do parser:**
1. A **pasta pai** (nome da série) tem prioridade sobre o nome do arquivo para identificação
2. Regex para P1: `^\[.*?\]\[(.+?)\]\s*-\s*Epis[óo]dio\s*(\d+)`
3. Regex para P2: `^\[.*?\]\s*(.+?)\s*-\s*Epis[óo]dio\s*(\d+)`
4. Regex para P3: `(\d{2})EP(\d{2})` → temporada + episódio
5. P4 (fallback): usar metadata do container (título) + nome da pasta
6. Normalizar: `_` → `'` (ex: `Mortal_s` → `Mortal's`), trim, title-case
7. Se parser falha → exibir nome raw do arquivo, permitir rename manual na UI

### RF-03: Pre-Processamento (Janela Deslizante)

**Modelo:** janela deslizante de **5 episódios não-assistidos** por série,
um perfil por vez (radio-switch: `local` OU `dlna`).

- [ ] Na tela de detail da série: botão "Pre-Processar" + radio-switch `[Local | DLNA]`
- [ ] Ao clicar: enfileira os **5 primeiros episódios não-assistidos** no perfil selecionado
- [ ] Pipeline: Decode → Frame Interp → FSR 4 Upscale → AMF Encode
- [ ] Frame interpolation: RIFE v4 (DirectML) primário, FSR 3 FG alternativo
- [ ] Upscale: FSR 4 SDK (DX12 Compute), fallback FSR 1
- [ ] Encode: AMF H.265, bitrate configurável por perfil
- [ ] Output: `{processed_folder}/{episodeId}_{profile}.mp4`
- [ ] Progresso por etapa (decode / interp / upscale / encode) com % e ETA
- [ ] Processamento em background (não bloqueia UI)
- [ ] **Auto-rotação:** ao marcar episódio como assistido:
  1. Deletar arquivo processado do episódio assistido
  2. Enfileirar próximo episódio não-assistido (mantém janela de 5)
- [ ] **Cleanup on close:** ao fechar o app, deletar TODOS os arquivos processados
  - Serviço de cleanup roda em background (não bloqueia shutdown)
  - Log de cleanup (arquivos deletados, espaço liberado)
  - Se cleanup falhar, retry na próxima inicialização
- [ ] Cancelar processamento ativo (botão na UI)
- [ ] Re-processar se arquivo original mudar (hash check)
- [ ] Log detalhado por job (tempos, frames, erros)
- [ ] **Uma série por vez:** processar episódios de uma série; se usuário abre
  outra série e clica pre-processar, pausar fila atual e iniciar nova
  (ou configurar: processar múltiplas séries em paralelo)

### RF-04: Gerenciamento de Armazenamento

**Modelo efêmero:** arquivos processados são cache temporário.
Cleanup total ao fechar o app. Storage máximo = janela de 5 × 1 perfil.

- [ ] Pasta de processados configurável (default: `%AppData%/CATRA/processed/`)
- [ ] Exibir uso de disco atual na tela de fila
- [ ] Estimativa de tamanho antes de processar (baseado em duração + perfil)
- [ ] Alerta se espaço livre < tamanho estimado da janela completa
- [ ] **Cleanup on close:** deletar todos os processados ao fechar
- [ ] **Cleanup on startup:** se app fechou sem cleanup (crash), limpar na inicialização
- [ ] Nunca deletar arquivo em uso (playing/streaming)

**Storage máximo (janela de 5, 1 perfil, 22min/ep):**

| Perfil | Resolução | FPS | Bitrate ~ | Por ep | **5 eps** |
|---|---|---|---|---|---|
| local | 1080p | 135 | 20 Mbps | ~3.3 GB | **~16.5 GB** |
| dlna | 4K | 55 | 45 Mbps | ~7.4 GB | **~37 GB** |

Máximo absoluto: **~37 GB** (dlna, 5 eps). Gerenciável em qualquer disco.

### RF-05: Reprodução de Vídeo (Local)

- [ ] Reproduz arquivo processado (perfil `local`: 1080p 135fps H.265)
- [ ] Se arquivo processado não existe → oferecer processar ou reproduzir original
- [ ] Player embutido no app (não abre player externo)
- [ ] Tela cheia (F11 ou botão)
- [ ] Controles customizados:
  - Play/Pause, Stop, Seek bar, Volume, Tempo (atual/total)
  - Botão "Pular Abertura" (sempre visível)
  - Botão "Transmitir" (DLNA)
  - Seleção de áudio/legenda (se source MKV com múltiplas faixas)
  - Indicador: perfil do arquivo sendo reproduzido
- [ ] **Pular Abertura:** avança +1:25 (default), configurável por série
- [ ] Salvar timestamp de reprodução periodicamente (a cada 5s)
- [ ] Oferecer "Continuar de onde parou" se progresso < 85%
- [ ] Decode: FFmpeg D3D11VA (hw accel) — trivial para H.265 1080p
- [ ] Render: DX11 SwapChain → WPF HwndHost
- [ ] Audio: FFmpeg decode → WASAPI

### RF-06: Transmissão DLNA

- [ ] Discovery de dispositivos DLNA na rede via SSDP
- [ ] Listar dispositivos encontrados (nome + ícone)
- [ ] **Serve arquivo processado** (perfil `dlna`: 4K 55fps H.265)
- [ ] Se arquivo processado não existe → oferecer processar ou transmitir original
- [ ] HTTP server (Kestrel) com Range requests para seek
- [ ] UPnP AVTransport: SetAVTransportURI + Play
- [ ] Controles remotos: Play, Pause, Stop, Seek, Volume
- [ ] **Seek: instantâneo** (Range request no arquivo, sem pipeline real-time)
- [ ] Indicador de status (conectado, streaming, erro)
- [ ] **Modo original:** opção de transmitir arquivo raw sem processamento
- [ ] MIME type: `video/mp4` (H.265)

### RF-07: Controle de Assistidos

- [ ] Marcar como assistido automaticamente quando progresso ≥ threshold
- [ ] **Threshold:** `(duração - 1.5min abertura - 1.5min encerramento) / duração`
  - Ex: vídeo 22min → threshold = (22-3)/22 ≈ 86.4%
- [ ] Marcar/desmarcar manualmente via menu de contexto
- [ ] Separar visualmente assistidos de não-assistidos
- [ ] Na view de episódios: caminho virtual tipo pasta `Série > Assistidos > Ep X`
- [ ] Persistir em SQLite: file_path, watched (bool), progress_pct, last_position_sec, timestamp
- [ ] Progresso baseado no arquivo **original** (não no processado) — mapeamento de timestamp

### RF-08: Thumbnails e Capas

- [ ] Auto-extrair thumbnail do vídeo (frame em ~10% da duração, evita frame de copyright)
- [ ] Permitir configurar capa manual (imagem local ou URL)
- [ ] Cache de thumbnails em pasta local (`%AppData%/CATRA/thumbs/`)
- [ ] Fallback: placeholder genérico se extração falhar
- [ ] Extração via FFmpeg (seek + frame capture, sem decode completo)

### RF-09: Metadados Online (Fase 3)

- [ ] Buscar metadados em TMDB/AniList ao adicionar série/filme
- [ ] Match fuzzy pelo nome da pasta (normalizado)
- [ ] Se não encontrar correspondência boa → usar apenas metadata local, sem forçar
- [ ] Salvar: sinopse, ano, gênero, poster, backdrop
- [ ] Não bloquear uso do app se offline ou sem match

### RF-10: Legendas (Fase 3, baixa prioridade)

- [ ] MKV: listar faixas de legenda embutidas, permitir seleção
- [ ] Carregar .srt externo se presente na mesma pasta (mesmo nome do arquivo)
- [ ] Legendas renderizadas como overlay no playback (não queimadas no encode)
- [ ] No processamento: extrair faixas de legenda do MKV e salvar separadamente

### RF-11: Bluetooth / Controle Remoto (Fase 4)

- [ ] Parear com celular via Bluetooth
- [ ] Protocolo custom: comandos play/pause/seek/volume/skip
- [ ] Celular funciona como controle remoto enquanto vídeo transmite na TV

---

## Requisitos Não-Funcionais

- [ ] Seguir tema do Windows (light/dark) automaticamente
- [ ] Inicialização < 3s para bibliotecas de até 500 itens
- [ ] Scan incremental (não re-processar arquivos já catalogados)
- [ ] SQLite com WAL mode para performance de escrita
- [ ] Sem telemetria, sem cloud, 100% local
- [ ] Executável portátil (pode rodar de pendrive)
- [ ] Logs em arquivo rotativo (%AppData%/CATRA/logs/)
- [ ] Pre-processamento: não bloquear UI, progresso visível
- [ ] Pre-processamento: cancelável a qualquer momento
- [ ] Playback local: decode H.265 1080p 135fps < 5% GPU (hw decode)
- [ ] DLNA: zero GPU durante transmissão (só HTTP serve)

---

## Modelagem de Dados (SQLite)

```sql
-- Categorias (nível 1 da pasta)
CREATE TABLE Category (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL UNIQUE,
    FolderPath  TEXT NOT NULL,
    CreatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Séries e Filmes (nível 2 da pasta)
CREATE TABLE MediaItem (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    CategoryId      INTEGER NOT NULL REFERENCES Category(Id),
    Title           TEXT NOT NULL,
    RawFolderName   TEXT NOT NULL,
    FolderPath      TEXT NOT NULL,
    MediaType       TEXT NOT NULL DEFAULT 'series',  -- 'series' | 'movie'
    SkipIntroSec    REAL NOT NULL DEFAULT 85.0,
    CoverPath       TEXT,
    -- Metadados online (nullable, Fase 3)
    TmdbId          INTEGER,
    Synopsis        TEXT,
    Year            INTEGER,
    Genre           TEXT,
    PosterUrl       TEXT,
    CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Episódios / Arquivos de vídeo
CREATE TABLE Episode (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    MediaItemId     INTEGER NOT NULL REFERENCES MediaItem(Id),
    FileName        TEXT NOT NULL,
    FilePath        TEXT NOT NULL UNIQUE,
    SeasonNumber    INTEGER DEFAULT 1,
    EpisodeNumber   INTEGER,
    DisplayTitle    TEXT,
    DurationSec     REAL,
    FileSizeBytes   INTEGER,
    SourceFps       REAL,                    -- fps do arquivo original
    SourceWidth     INTEGER,                 -- resolução original
    SourceHeight    INTEGER,
    FileHash        TEXT,                    -- SHA-256 para detectar mudanças
    ThumbnailPath   TEXT,
    CreatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Arquivos processados (um por episódio por perfil)
CREATE TABLE ProcessedFile (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EpisodeId       INTEGER NOT NULL REFERENCES Episode(Id),
    Profile         TEXT NOT NULL,           -- 'local' | 'dlna'
    FilePath        TEXT NOT NULL,           -- caminho do arquivo processado
    FileSizeBytes   INTEGER,
    TargetFps       REAL NOT NULL,
    TargetWidth     INTEGER NOT NULL,
    TargetHeight    INTEGER NOT NULL,
    EncodeBitrate   INTEGER,                 -- kbps
    InterpMethod    TEXT,                    -- 'rife' | 'fsr3fg' | 'none'
    UpscaleMethod   TEXT,                    -- 'fsr4' | 'fsr1' | 'none'
    ProcessedAt     TEXT NOT NULL DEFAULT (datetime('now')),
    SourceHash      TEXT NOT NULL,           -- hash do original usado
    UNIQUE(EpisodeId, Profile)
);

-- Fila de processamento
CREATE TABLE ProcessJob (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EpisodeId       INTEGER NOT NULL REFERENCES Episode(Id),
    Profile         TEXT NOT NULL,           -- 'local' | 'dlna'
    Status          TEXT NOT NULL DEFAULT 'queued',
        -- 'queued' | 'processing' | 'completed' | 'failed' | 'cancelled'
    Priority        INTEGER NOT NULL DEFAULT 0,
    ProgressPct     REAL NOT NULL DEFAULT 0.0,
    CurrentStep     TEXT,                    -- 'decode' | 'interp' | 'upscale' | 'encode'
    ErrorMessage    TEXT,
    StartedAt       TEXT,
    CompletedAt     TEXT,
    CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(EpisodeId, Profile)
);

-- Estado de reprodução / assistido
CREATE TABLE WatchState (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EpisodeId       INTEGER NOT NULL UNIQUE REFERENCES Episode(Id),
    Watched         INTEGER NOT NULL DEFAULT 0,
    ProgressPct     REAL NOT NULL DEFAULT 0.0,
    LastPositionSec REAL NOT NULL DEFAULT 0.0,  -- posição no arquivo ORIGINAL
    UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Configurações do app
CREATE TABLE AppSettings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
```

**AppSettings keys:**
```
root_folder                 -- pasta raiz da biblioteca
processed_folder            -- pasta dos arquivos processados
window_size                 -- episódios na janela deslizante (default: 5)
cleanup_on_close            -- 'true' | 'false' (default: true)
local_target_fps            -- default: 135
local_target_width          -- default: 1920
local_target_height         -- default: 1080
local_encode_bitrate_kbps   -- default: 20000
dlna_target_fps             -- default: 55
dlna_target_width           -- default: 3840
dlna_target_height          -- default: 2160
dlna_encode_bitrate_kbps    -- default: 45000
interp_method               -- 'rife' | 'fsr3fg' (default: rife)
upscale_method              -- 'fsr4' | 'fsr1' (default: fsr4)
default_skip_intro_sec      -- default: 85
theme_override              -- 'system' | 'light' | 'dark'
```

---

## Interface (UI/UX)

### Tela 1: Home / Biblioteca

```
┌─────────────────────────────────────────────────────┐
│  CATRA          [🔍 Buscar]  [🔄 Atualizar]  [⚙️]  │
├─────────────────────────────────────────────────────┤
│                                                     │
│  Categorias (sidebar ou tabs):                      │
│  [Animes] [Filmes] [Séries] [Todos]                │
│                                                     │
│  ┌──────┐  ┌──────┐  ┌──────┐  ┌──────┐           │
│  │ CAPA │  │ CAPA │  │ CAPA │  │ CAPA │           │
│  │      │  │      │  │      │  │      │           │
│  │Nome  │  │Nome  │  │Nome  │  │Nome  │           │
│  │12 eps│  │Filme │  │8 eps │  │24 eps│           │
│  │▶ 3/12│  │      │  │✓     │  │▶ cont│           │
│  └──────┘  └──────┘  └──────┘  └──────┘           │
│                                                     │
│  Cards: capa, título, qtd eps, progresso            │
│  Badge "Continuar Assistindo"                       │
│  Badge "⚙ Processando..." se job ativo             │
│  Badge "✓ Processado" / "○ Original" por perfil    │
└─────────────────────────────────────────────────────┘
```

### Tela 2: Detail da Série/Filme

```
┌─────────────────────────────────────────────────────┐
│  ← Voltar    A Record of a Mortal's Journey   [⚙️] │
├─────────────────────────────────────────────────────┤
│  [CAPA GRANDE]   Sinopse...                         │
│                  Ano: 2024 | Gênero: Ação           │
│                  Skip Intro: [1:25 ▼]               │
│                                                     │
│  Pre-Processar:  (●) Local  ( ) DLNA   [📥 Iniciar]│
│  Janela: 5 episódios | ~16.5 GB estimados           │
│                                                     │
│  ── Não Assistidos ──────────────────────           │
│  ┌──────┐  ┌──────┐  ┌──────┐                      │
│  │ EP01 │  │ EP02 │  │ EP03 │  ...                 │
│  │ ▶    │  │ ▶    │  │ ▶    │                      │
│  │⚙ fila│  │⚙ fila│  │○     │  (na janela/fila)   │
│  └──────┘  └──────┘  └──────┘                      │
│                                                     │
│  ── Assistidos ──────────────────────               │
│  ┌──────┐  ┌──────┐                                │
│  │ EP24 │  │ EP25 │                                │
│  │ ✓    │  │ ✓    │                                │
│  └──────┘  └──────┘                                │
│                                                     │
│  Context menu: Marcar assistido, Renomear,          │
│    Configurar capa, Detalhes                        │
└─────────────────────────────────────────────────────┘
```

### Tela 3: Fila de Processamento (por série)

```
┌─────────────────────────────────────────────────────┐
│  📥 Pre-Processamento — A Record of a Mortal's...   │
├─────────────────────────────────────────────────────┤
│  Perfil: Local (1080p 135fps)                       │
│  Janela: 5 episódios | Disco usado: 9.8 / 16.5 GB  │
│                                                     │
│  ┌─ Processando agora ─────────────────────────┐   │
│  │ EP03                                        │   │
│  │ [████████████░░░░░░░░] 58% — Upscale (FSR4) │   │
│  │ ETA: ~3 min  |  [✕ Cancelar]               │   │
│  └─────────────────────────────────────────────┘   │
│                                                     │
│  ┌─ Na janela ─────────────────────────────────┐   │
│  │ ✓ EP01  3.1 GB  (pronto)                    │   │
│  │ ✓ EP02  3.2 GB  (pronto)                    │   │
│  │ ⚙ EP03  ...     (processando)               │   │
│  │ ○ EP04  ~3.3 GB (na fila)                   │   │
│  │ ○ EP05  ~3.3 GB (na fila)                   │   │
│  └─────────────────────────────────────────────┘   │
│                                                     │
│  Ao assistir EP01 → deleta EP01, enfileira EP06    │
│  Ao fechar o app → deleta todos os processados      │
└─────────────────────────────────────────────────────┘
```

### Tela 4: Player

```
┌─────────────────────────────────────────────────────┐
│                                                     │
│                    VÍDEO                            │
│           (1080p 135fps, processado)                │
│                                                     │
├─────────────────────────────────────────────────────┤
│  ▶️  ⏸  ⏹   ━━━━━━━━━●━━━━━━━━━  12:34 / 22:00   │
│  🔊 ━━━●━━   [⏭ Pular Abertura]  [📺 Transmitir]  │
│  [CC] [🔊 Áudio] [⛶ Fullscreen]  [🖥 1080p135]    │
└─────────────────────────────────────────────────────┘
```

### Tela 5: Transmissão Ativa

```
┌─────────────────────────────────────────────────────┐
│  📺 Transmitindo para: Samsung TV [Living Room]     │
│  ━━━━━━━━━●━━━━━━━━━  12:34 / 22:00               │
│  Arquivo: 4K 55fps H.265 | 45 Mbps | HTTP serve    │
│                                                     │
│  [▶/⏸]  [⏹ Parar]  [🔊 Vol]                      │
│                                                     │
│  (zero GPU — só HTTP file serving)                  │
└─────────────────────────────────────────────────────┘
```

### Tela 6: Settings

```
┌─────────────────────────────────────────────────────┐
│  ⚙️ Configurações                                   │
├─────────────────────────────────────────────────────┤
│  Biblioteca                                         │
│    Pasta raiz: [C:\Media\_______________] [Browse]  │
│    Auto-scan: [✓]                                   │
│                                                     │
│  Processamento                                      │
│    Janela de episódios: [5]                         │
│    Método interpolação: [RIFE v4 ▼]                │
│    Método upscale: [FSR 4 ▼]                       │
│                                                     │
│    Perfil Local:                                    │
│      Resolução: [1920] x [1080]                     │
│      FPS: [135]                                     │
│      Bitrate: [20000] kbps                          │
│                                                     │
│    Perfil DLNA:                                     │
│      Resolução: [3840] x [2160]                     │
│      FPS: [55]                                      │
│      Bitrate: [45000] kbps                          │
│                                                     │
│  Armazenamento                                      │
│    Pasta processados: [%AppData%/CATRA/processed/]  │
│    Limpar ao fechar: [✓] (recomendado)              │
│    Uso atual: 9.8 GB  [Limpar agora]                │
│                                                     │
│  DLNA                                               │
│    Porta HTTP: [auto]                               │
│    Transmitir processado por padrão: [✓]            │
│                                                     │
│  Player                                             │
│    Skip intro padrão: [1:25]                        │
│    Tema: [Seguir Windows ▼]                         │
└─────────────────────────────────────────────────────┘
```

---

## Regras de Negócio

### RN-01: Detecção Série vs Filme
- Pasta com **1 arquivo de vídeo** → `movie`
- Pasta com **2+ arquivos de vídeo** → `series`
- Override manual possível via UI

### RN-02: Threshold de Assistido
```
threshold_pct = (duration_sec - 90 - 90) / duration_sec
// 90s abertura + 90s encerramento
// Se duration < 300s (5min), threshold = 90%
// Se duration < 180s (3min), threshold = 95%
```

### RN-03: Continuar Assistindo
- Se `progress_pct < 85%` E `last_position_sec > 30` → mostrar "Continuar"
- Badge no card da série na Home
- Ao clicar em episódio com progresso → dialog "Continuar de MM:SS?"

### RN-04: Skip Intro
- Default global: 85 segundos (1:25)
- Override por série: campo `SkipIntroSec` em `MediaItem`
- Botão sempre visível no player
- Ao clicar: `current_position += skip_intro_sec`
- Desabilitar se `current_position + skip > duration - 30`

### RN-05: Thumbnail Anti-Copyright
- Extrair frame em **10% da duração** (evita logo/abertura)
- Se vídeo < 5min → frame em 30%
- Permitir re-extração e override manual

### RN-06: Normalização de Nomes
- `_` → `'` (possessivo)
- Remover tags `[Site]` do display
- Title Case para display
- Manter raw para referência

### RN-07: Pipeline de Processamento — Decisões
```
SE source_fps >= target_fps → SKIP interpolação
SE source_height >= target_height → SKIP upscale
SE source já é H.265 E fps/res batem → SKIP re-encode (copy)

Exemplos:
  720p 24fps → local (1080p 135fps):  interp ✓  upscale ✓
  720p 24fps → dlna  (4K 55fps):      interp ✓  upscale ✓
  1080p 24fps → local (1080p 135fps): interp ✓  upscale ✗
  1080p 24fps → dlna  (4K 55fps):     interp ✓  upscale ✓
  1080p 60fps → local (1080p 135fps): interp ✓  upscale ✗
  4K 24fps → dlna (4K 55fps):         interp ✓  upscale ✗
```

### RN-08: Mapeamento de Timestamp (Original ↔ Processado)
- WatchState salva posição no arquivo **original** (portável se re-processar)
- Conversão: `processed_pos = original_pos` (mesma duração, só fps/res diferente)
- Duração é idêntica — interpolação adiciona frames, não muda tempo total

### RN-09: Reprocessamento
- Se `Episode.FileHash != ProcessedFile.SourceHash` → marcar como stale
- Badge "↻" no card do episódio
- Se episódio stale está na janela → re-enfileirar automaticamente
- Reprocessamento substitui arquivo anterior (delete + novo)

### RN-10: Janela Deslizante de Pre-Processamento
```
CONSTANTES:
  WINDOW_SIZE = 5 (configurável em Settings)

AO ABRIR SÉRIE + CLICAR "Pre-Processar":
  perfil = radio-switch selecionado (local | dlna)
  não_assistidos = episódios WHERE watched = 0 ORDER BY EpisodeNumber
  janela = não_assistidos[0..WINDOW_SIZE-1]
  enfileirar janela com perfil selecionado

AO MARCAR EPISÓDIO COMO ASSISTIDO:
  SE episódio tem ProcessedFile:
    deletar arquivo processado (se não está em uso)
    deletar registro ProcessedFile
  SE janela ativa para esta série:
    próximo = primeiro não-assistido FORA da janela atual
    SE próximo existe:
      enfileirar próximo (mantém WINDOW_SIZE)

AO FECHAR O APP:
  cancelar jobs ativos
  deletar TODOS os arquivos em ProcessedFile
  limpar tabela ProcessedFile e ProcessJob
  log: "Cleanup: N arquivos deletados, X GB liberados"

AO ABRIR O APP (cleanup residual):
  SE existem arquivos órfãos em processed_folder:
    deletar todos (crash recovery)
```

---

## Integração

### Native Bridge (C++ DLL)

```cpp
// catra_gpu.h — API C flat para P/Invoke

extern "C" {
    // === Lifecycle ===
    int  catra_init(void* d3d11_device);
    void catra_shutdown();
    int  catra_get_upscale_mode();     // 0=off, 1=fsr1, 2=fsr4
    int  catra_is_fsr4_available();
    int  catra_get_interp_method();    // 0=none, 1=rife, 2=fsr3fg

    // === Frame Interpolation ===
    // Cria contexto de interpolação para um job
    int  catra_interp_create(int src_w, int src_h, double src_fps,
                             double target_fps, int method,
                             int* out_ctx);
    // Processa um par de frames → gera N frames intermediários
    // Retorna quantidade de frames gerados
    int  catra_interp_process(int ctx,
                              void* frame_a, void* frame_b,
                              void** out_frames, int* out_count);
    void catra_interp_destroy(int ctx);

    // === Upscale (FSR 4 / FSR 1) ===
    int  catra_upscale_create(int src_w, int src_h,
                              int dst_w, int dst_h,
                              int method, int* out_ctx);
    int  catra_upscale_process(int ctx,
                               void* src_texture,
                               void** dst_texture);
    void catra_upscale_destroy(int ctx);

    // === Encode (AMF H.265) ===
    int  catra_encode_create(int width, int height,
                             int bitrate_kbps, double fps,
                             int* out_ctx);
    int  catra_encode_frame(int ctx, void* texture,
                            uint8_t** out_buf, int* out_size);
    int  catra_encode_flush(int ctx, uint8_t** out_buf, int* out_size);
    void catra_encode_destroy(int ctx);
}
```

### FFmpeg Integration (C# via FFmpeg.AutoGen)

- Decode: `avcodec_send_packet` / `avcodec_receive_frame` com `hw_device_ctx` D3D11VA
- Output: `AVFrame.data[0]` = `ID3D11Texture2D*` (textura GPU, zero-copy)
- Seek: `av_seek_frame` + flush decoder
- Audio: decode separado → WASAPI via NAudio ou CSCore
- Thumbnail: `av_seek_frame(10%)` → decode 1 frame → `sws_scale` → save PNG
- Mux: `avformat_write_header` / `av_write_frame` para MP4 H.265

### Pipeline Orchestration (C#)

```
ProcessJob(episodeId, profile):
  1. Open source → read metadata (fps, res, duration)
  2. Decide steps (RN-07): need interp? need upscale?
  3. catra_interp_create(src_fps → target_fps)
  4. catra_upscale_create(src_res → target_res)
  5. catra_encode_create(target_res, target_fps, bitrate)
  6. Loop frames:
     a. FFmpeg decode → texture
     b. catra_interp_process(frameA, frameB) → interp frames
     c. for each interp frame:
        - catra_upscale_process(frame) → upscaled
        - catra_encode_frame(upscaled) → H.265 NAL
        - av_write_frame → output file
     d. Update ProcessJob.ProgressPct
  7. catra_encode_flush → final NALs
  8. Mux audio: FFmpeg decode audio → AAC → mux into output
  9. Update ProcessedFile record
  10. Cleanup contexts
```

### HTTP Server (Kestrel Embedded)

- `GET /media/{episodeId}?profile=dlna` — serve arquivo processado (Range)
- `GET /media/{episodeId}?profile=raw` — serve arquivo original (Range)
- Bind: `0.0.0.0:{porta_aleatória}` (acessível na LAN)
- Firewall: pedir exceção no primeiro run

### DLNA/UPnP

- **Discovery:** SSDP M-SEARCH via UDP multicast (`239.255.255.250:1900`)
- **Library:** Rssdp para discovery, SOAP manual para AVTransport
- **Serviços UPnP:**
  - `AVTransport` (SetAVTransportURI, Play, Pause, Stop, Seek)
  - `RenderingControl` (SetVolume, GetVolume)
  - `ConnectionManager` (GetProtocolInfo)
- **Samsung:** DLNA padrão, MIME `video/mp4` (H.265)

---

## Critérios de Aceitação

### MVP — Fase 1

- [ ] App abre e escaneia pasta raiz, exibindo cards por categoria
- [ ] Parser extrai nome/episódio dos 4 padrões identificados
- [ ] Clique em série → lista episódios separados (assistidos / não assistidos)
- [ ] Reprodução de MP4, AVI, MKV (arquivo original, sem processamento)
- [ ] Controles customizados funcionam (play/pause/seek/volume/fullscreen)
- [ ] Botão "Pular Abertura" avança 1:25 (configurável por série)
- [ ] Progresso salvo e "Continuar" funciona
- [ ] Threshold de assistido calculado corretamente
- [ ] Marcar/desmarcar assistido manualmente
- [ ] Transmissão DLNA para Samsung Smart TV (arquivo original)
- [ ] Tema segue Windows (light/dark)
- [ ] Thumbnails auto-geradas
- [ ] SQLite persiste estado entre sessões
- [ ] UI de Settings funcional (pasta raiz, skip intro, tema)

### Fase 2 — Pre-Processamento GPU

- [ ] Native bridge C++ compila e inicializa (FSR 4 + RIFE + AMF)
- [ ] Frame interpolation RIFE: 24fps → 135fps e 24fps → 55fps
- [ ] FSR 4 upscale: 720p/1080p → 4K (DX12 Compute, RDNA 4)
- [ ] AMF H.265 encode funcional
- [ ] Pipeline completo: decode → interp → upscale → encode → MP4
- [ ] Fila de processamento com progresso por etapa
- [ ] Dois perfis (local 1080p 135fps, dlna 4K 55fps)
- [ ] Processamento em background sem bloquear UI
- [ ] Cancelar job funcional
- [ ] Playback usa arquivo processado quando disponível
- [ ] DLNA serve arquivo processado quando disponível
- [ ] Fallback: reproduzir/transmitir original se não processado
- [ ] Janela deslizante: auto-deletar assistido + enfileirar próximo (mantém 5)
- [ ] Cleanup on close: deletar todos os processados ao fechar
- [ ] Cleanup on startup: limpar órfãos de crash anterior

### Fase 3 — Enhancements

- [ ] Metadados online (TMDB/AniList) com match fuzzy
- [ ] Legendas MKV + .srt externo
- [ ] Busca/filtro na biblioteca
- [ ] Reprocessamento automático quando original muda

### Fase 4 — Futuro

- [ ] Bluetooth + controle remoto via celular
- [ ] Múltiplos perfis de usuário

---

## Fases de Entrega (Detalhadas)

### Fase 1 — MVP (Core App + Playback + DLNA Raw)
1. Setup projeto WPF + estrutura de camadas + DI
2. SQLite + modelos de dados + repositories
3. Scanner de biblioteca + parser de nomes (4 padrões)
4. UI Home (cards por categoria) + Detail (episódios)
5. FFmpeg decode D3D11VA + render DX11 SwapChain → WPF HwndHost
6. Controles customizados (play/pause/seek/volume/fullscreen/skip intro)
7. Watch state (threshold dinâmico + manual + continuar)
8. DLNA casting raw (discovery + AVTransport + HTTP file serve)
9. Thumbnails (FFmpeg frame capture)
10. Theme Windows (light/dark)
11. Settings UI (pasta raiz, skip intro, tema)

### Fase 2 — Pre-Processamento GPU
12. Native bridge C++ (skeleton + build system CMake + vcpkg)
13. RIFE v4 integration (ONNX Runtime + DirectML)
14. FSR 4 SDK integration (DX12 Compute)
15. D3D11→DX12 texture interop
16. AMF H.265 encoder integration
17. Pipeline orchestration C# (decode → interp → upscale → encode → mux)
18. Janela deslizante (queue de 5, auto-rotação, progresso, cancel)
19. UI: radio-switch perfil + botão pre-processar + tela de fila
20. Playback/DLNA: usar processado quando disponível
21. Cleanup on close + cleanup on startup (crash recovery)
22. Settings: perfis de processamento + janela

### Fase 3 — Enhancements
23. Metadados online (TMDB/AniList)
24. Legendas MKV + .srt
25. Busca/filtro
26. Reprocessamento automático (hash check)

### Fase 4 — Futuro
27. Bluetooth controle remoto
28. Múltiplos perfis

---

## Riscos e Considerações

| Risco | Impacto | Mitigação |
|---|---|---|
| **FSR 4 SDK instabilidade** (SDK recente) | **Crítico** | Validar API no step 14; fallback FSR 1 |
| **RIFE DirectML em RDNA 4** | Alto | ONNX Runtime suporta DirectML; testar cedo (step 13) |
| **D3D11→DX12 interop** | Alto | Shared textures + keyed mutex é padrão; prototipar |
| **FFmpeg.AutoGen memory mgmt** | Alto | Wrapper robusto, dispose patterns, testes |
| **AMF encoder integration** | Médio | AMF SDK maduro; usar via native bridge |
| **Storage (~37GB max)** | Baixo | Janela de 5 + cleanup on close; máximo ~37GB (dlna) |
| **DLNA Samsung + H.265** | Médio | Testar com TV real; H.265 MP4 é padrão DLNA |
| **Parser de nomes frágil** | Médio | Fallback gracioso, rename manual |
| **Firewall Windows** | Baixo | Pedir exceção no primeiro run |
| **C++ DLL build/deploy** | Médio | CMake + vcpkg; distribuir DLLs junto do exe |
| **Tempo de processamento** | Baixo | User aceitou minutos; progresso + ETA visíveis |

---

## Estrutura de Pastas do Projeto

```
Reprodutor_CATRA/
├── src/
│   ├── CATRA.App/                  # WPF application (startup, DI, windows)
│   ├── CATRA.Core/                 # Domain models, interfaces, enums
│   ├── CATRA.Services/             # Business logic
│   │   ├── Library/                # Scanner, parser, watcher
│   │   ├── Playback/               # FFmpeg decode, render, audio
│   │   ├── Processing/             # Pipeline orchestration, queue, jobs
│   │   ├── Casting/                # DLNA, SSDP, HTTP server
│   │   ├── Storage/                # Eviction, space management
│   │   └── Metadata/               # Thumbnails, online metadata (Fase 3)
│   ├── CATRA.Data/                 # SQLite, repositories
│   └── CATRA.UI/                   # Views, ViewModels, Controls, Themes
├── native/
│   └── catra-gpu/                  # C++ DLL (FSR 4, RIFE, AMF, DX12)
│       ├── CMakeLists.txt
│       ├── catra_gpu.h             # C ABI header
│       ├── interp_rife.cpp         # RIFE v4 (ONNX/DirectML)
│       ├── interp_fsr3fg.cpp       # FSR 3 FG (alternativo)
│       ├── upscale_fsr4.cpp        # FSR 4 DX12 Compute
│       ├── upscale_fsr1.cpp        # FSR 1 fallback
│       ├── encode_amf.cpp          # AMF H.265 encoder
│       └── d3d_interop.cpp         # D3D11↔DX12 texture sharing
├── lib/
│   ├── ffmpeg/                     # FFmpeg binaries + headers
│   ├── fsr-sdk/                    # FidelityFX FSR 4 SDK (GPUOpen)
│   ├── rife/                       # RIFE v4 ONNX models
│   ├── onnxruntime/                # ONNX Runtime + DirectML
│   └── amf/                        # AMD AMF SDK headers
├── tests/
│   ├── CATRA.Core.Tests/
│   ├── CATRA.Services.Tests/
│   └── CATRA.Data.Tests/
└── .agents/                        # Specs, skills, agents
```

---

## Decisões Registradas

| # | Decisão | Motivo |
|---|---|---|
| D1 | WPF sobre WinUI 3 | WinUI 3 MediaPlayer limitado, WPF + pipeline custom mais flexível |
| D2 | **Pre-processamento offline sobre real-time** | User aceita esperar minutos; elimina complexidade real-time; DLNA vira file serving |
| D3 | **Pipeline custom sobre libmpv** | FSR 4 + RIFE requerem integração direta no pipeline GPU |
| D4 | **FSR 4 SDK direto, não driver upgrade** | Driver upgrade (3.1→4) só funciona para jogos DX12 |
| D5 | **DX12 Compute, não Vulkan** | FSR 4 é DX12 only |
| D6 | **RIFE primário, FSR 3 FG alternativo** | RIFE é feito pra vídeo (qualidade superior); FSR 3 FG é pra jogos |
| D7 | DLNA sobre Chromecast | TV Samsung nativa DLNA |
| D8 | **DLNA serve processado, não transcode real-time** | Pré-processado = zero GPU durante transmissão, seek instantâneo |
| D9 | AMF sobre NVENC/libx264 | GPU AMD RDNA 4; AMF é encoder nativo |
| D10 | SQLite sobre JSON | Query, WAL, integridade relacional |
| D11 | Parser regex sobre IA | Determinístico, rápido, offline, debugável |
| D12 | C++ DLL (C ABI) sobre C++/CLI | Sem dependência CLR, interop limpo |
| D13 | FSR 1 como fallback de upscale | Garante funcionalidade se FSR 4 falhar |
| D14 | FFmpeg.AutoGen sobre CLI | Zero-copy GPU textures (D3D11VA) |
| D15 | **Janela deslizante de 5 + cleanup on close** | Storage efêmero (~37GB max); sem gestão complexa; app fecha = cache limpo |
| D16 | **WatchState no timestamp original** | Portável entre reprocessamentos; duração não muda |
| D17 | **Perfis separados (local/dlna)** | 1080p 135fps local vs 4K 55fps DLNA são targets distintos |

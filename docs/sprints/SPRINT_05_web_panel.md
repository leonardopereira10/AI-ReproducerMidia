# SPRINT 05 — Web Control Panel + Streaming Home

> Síntese consolidada dos arquivos de sprint (`.agents/sprint_atual` da época).
> Artefatos originais completos: `.agents/Learning/sprints/SPRINT_05/`.
> Commits principais: `e3ffc6e` (Web Control Panel), `2c20275` (Streaming Home), `64c6e91` (navegação por categoria).

## Objetivo

Permitir controlar o CATRA e assistir mídia remotamente a partir de um painel
web mobile-first (celular/tablet na mesma rede), com descoberta via DLNA.

## O que foi entregue

### 1. Web Control Panel (`src/CATRA.Services/WebControl/`)
- Servidor web embutido (`WebControlServer`) com API de controle remoto:
  play/pause, seek, volume, navegação do player.
- `WebControlService` orquestra estado (`WebControlState`) e integração com o
  engine de playback.
- Descoberta DLNA para o dispositivo aparecer na rede local.

### 2. Streaming Home (`src/CATRA.Services/Streaming/`, `Library/`)
- **API de biblioteca REST** (`LibraryApiService` + endpoints no
  `WebControlServer`): categorias com contagem de itens, itens por categoria
  (séries/filmes), episódios com perfis de processamento disponíveis, busca,
  "continue assistindo". JSON camelCase.
- **StreamService** (`IStreamService`): serving HTTP de mídia para player HTML5,
  com perfis de resolução/qualidade e lifecycle de token
  (fast-start + expiração).
- **Browser mode**: roteamento via WebSocket para comandos do player web
  (`switch profile`, controle de playback) refletidos no app desktop.
- Integração com casting (`subtask_12_cast_integration`) e thumbnails via URL
  no app WPF (`ThumbnailService`).

### 3. Frontend web
- Home mobile-first com biblioteca, busca e continue-assistindo.
- Player HTML5 com seleção de perfil, controles remotos e sincronização de
  estado com o app via WebSocket.

### 4. Tracking de uso
- `ProcessedFileUsage` registra uso dos arquivos processados pelos streams
  (perfis servidos), alimentando decisões de retenção/limpeza.

## Arquitetura (resumo)

```
Browser mobile ──HTTP──▶ WebControlServer ──▶ LibraryApiService (biblioteca)
      │                        │
      │◀────WebSocket──────────┘  (comandos player, browser mode)
      │
      └──HTTP stream──▶ StreamService ──▶ IMediaHttpServer (arquivo/perfil)
```

## Convenções e decisões relevantes

- Endpoints REST em camelCase; naming `{recurso}/{ação}`.
- Tokens de stream com lifecycle gerenciado (fast-start para não travar o
  início do playback).
- View intermediária de itens por categoria (`64c6e91`): categorias → itens →
  episódios, em vez de categorias → episódios direto.
- DI registra Library/Streaming/WebControl em composição única no app
  (`subtask_15`).

## Lições / pontos de atenção

- Sincronização de estado app↔web é sensível a race (play/pause toggle já
  apresentou bug corrigido em `a4c38ce`).
- `WebControlState` centraliza estado; evitar estado duplicado em handlers.

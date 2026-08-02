# SPRINT_02 — Fase 2: Pre-Processamento GPU

**Tag:** v0.2.0
**Período:** 2026-08-02
**Status:** ✅ CONCLUÍDA

## Resumo

Fase 2 implementa o pipeline completo de pre-processamento GPU: decode → RIFE v4
interpolação → FSR 4 upscale → AMF H.265 encode → mux, com janela deslizante,
integração player/DLNA, cleanup e settings.

## Subtasks (11/11)

| ST | Título | Commit | Retries |
|----|--------|--------|---------|
| ST-12 | Native bridge C++ skeleton | 5886c0e | 1 (timeout) |
| ST-13 | RIFE v4 frame interpolation | 3959069 | 1 (rejeitado→fix USE_DML/leak) |
| ST-14 | FSR 4 upscale + FSR 1 fallback | 9adfd23 | 1 (timeout) |
| ST-15 | D3D11↔DX12 texture interop | e79052b | 1 (timeout) |
| ST-16 | AMF H.265 encoder | baa56f2 | 1 (timeout) |
| ST-17 | Pipeline orchestration C# | 89b8b1f | 2 (CS8122 + ownership fix) |
| ST-18 | Janela deslizante | f8a110b | 1 (timeout) |
| ST-19 | UI pre-processar | 485a06d | 1 (timeout) |
| ST-20 | Playback/DLNA usar processado | f96d8b1 | 0 (retomada) |
| ST-21 | Cleanup on close + startup | bfbe1c5 | 0 |
| ST-22 | Settings processamento + perfis | 5d4122b | 0 |

**Commits de suporte:** 52366d6 (keyed mutex fix), 9be098a (VM dispose fix), f67d9d6 (docs)

## Testes

- **Total:** 541 (Core 2 + UI 124 + Data 20 + Services 395)
- **Build:** 0 warnings / 0 errors (Debug + Release)
- **Delta vs Fase 1:** +64 testes (477 → 541)

## Critérios Verificados

- RF-03: Pipeline completo (decode→interp→upscale→encode→mux)
- RN-07: Janela deslizante (queue + rotação)
- RN-08: Timestamp mapping 1:1
- RN-09: Stale detection (hash check)
- RN-10: Cleanup on close + startup
- Tela 3: Profile indicator + fallback dialog
- Tela 6: Settings Processamento habilitada

## Validação MANUAL Pendente (hardware)

1. Build nativo real (build-native.ps1 + VS2022 + vcpkg + SDKs)
2. Playback D3D11VA real (decode hardware)
3. Encode H.265 real (AMF runtime)
4. RIFE/FSR4 em GPU RDNA4 (ONNX/DirectML)
5. DLNA com TV real (UPnP)
6. Leak GPU em batch de 5 episódios (VRAM monitoring)

## Lições

- **Timeout padrão:** subtasks grandes estouram turn budget com trabalho PRONTO — verificar build+test e seguir, não re-delegar
- **Ownership nativo:** caller-owned (catra_release_texture/catra_free) resolveu blocker ST-17
- **Dispose sistêmico:** VMs precisam Dispose explícito na navegação (fix 9be098a)
- **Keyed mutex:** texturas compartilhadas D3D11↔DX12 exigem sync explícito (fix 52366d6)
- **Retomada de sessão:** script RESUME_FASE2.md funcionou — working tree preservado, diagnóstico rápido

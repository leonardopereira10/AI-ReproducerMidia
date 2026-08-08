# Sprint Log — CATRA Fase 2 (Pre-Processamento GPU)

**Fase 1:** CONCLUÍDA — tag v0.1.0, 11/11 subtasks, arquivada em .agents/Learning/sprints/SPRINT_01/
**Fase 2 início:** 2026-08-02
**Testes atuais:** 542 (Core 2 + UI 124 + Data 20 + Services 396) — build 0w/0e

## ⚠️ Restrição ambiental (Fase 2)
- SEM MSVC (cl.exe) / SEM VS2022 / SEM vcpkg → **build nativo C++ NÃO verificável** (validação por INSPEÇÃO + scripts/build-native.ps1 documentado p/ máquina com toolchain)
- FSR 4 SDK license-gated (GPUOpen) → não obtível automaticamente
- Padrão: código nativo completo + inspeção; camada C# testada com fakes; playback/TV/GPU reais = validação MANUAL

## Status
| ST | Título | Dev | Review | QA | Commit | Retries |
|----|--------|-----|--------|----|--------|---------|
| ST-12 | Native bridge C++ skeleton | ✅ | ✅ | ✅ | 5886c0e | 1 (timeout) |
| ST-13 | RIFE v4 interp | ✅ | ✅ (re-review) | ✅ | 3959069 | 1 (rejeitado→fix USE_DML/leak) |
| ST-14 | FSR 4 upscale + FSR1 fallback | ✅ | ✅ | ✅ | 9adfd23 | 1 (timeout) |
| ST-15 | D3D11↔DX12 interop | ✅ | ✅ | ✅ | e79052b | 1 (timeout) |
| ST-16 | AMF H.265 encoder | ✅ | ✅ | ✅ | baa56f2 | 1 (timeout) |
| ST-17 | Pipeline orchestration C# | ✅ | ✅ (re-review) | ✅ | 89b8b1f | 2 (CS8122 fix + ownership blocker/major fix) |
| ST-18 | Janela deslizante | ✅ | ✅ | ✅ | f8a110b | 1 (timeout) |
| ST-19 | UI pre-processar | ✅ | ✅ (ressalva) | ✅ | 485a06d | 1 (timeout) |
| (fix) | Dispose VMs on navigation | ✅ | — | — | 9be098a | follow-up leak sistêmico |
| (fix) | RIFE ORT version negotiation + graceful degradation test | ✅ | — | ✅ (build+test) | pendente | — |
| ST-20 | Playback/DLNA usar processado | 🔄 EM ANDAMENTO | — | — | — | 0 |
| ST-21 | Cleanup on close + startup | ⏳ | — | — | — | 0 |
| ST-22 | Settings processamento | ⏳ | — | — | — | 0 |

## Lotes (Fase 2) — sequenciais (arquivos nativos/UI compartilhados)
1. ST-12 ✅ | 2. ST-13 ✅ → ST-14 ✅ | 3. ST-15 ✅ → ST-16 ✅ | 4. ST-17 ✅ | 5. ST-18 ✅ | 6. ST-19 ✅ → ST-20 🔄 | 7. ST-21 → ST-22

## Padrões estabelecidos (reusar)
- Loop: dev → review (builtin) → QA (qa-tester-{nivel}) → commit `feat(ST-nn): titulo`
- Dev: async, control watchdog, acceptance "checked", turnBudget 40-55 p/ subtasks grandes, timeoutMs 900k-1200k
- Timeout no fim c/ trabalho pronto = comum: verificar build+test direto, se verde prosseguir p/ review+QA
- Review/QA em chain sequencial [reviewer, qa-tester-{nivel}] (evita build paralelo no mesmo cwd)
- Fixes de review: re-delegar dev com issues exatos; re-review focado depois
- Nativo: GuardCabi catch(...)→CATRA_ERR_UNKNOWN, RAII/ComPtr, keyed mutex em texturas compartilhadas, catra_release_texture/catra_free p/ ownership caller-owned
- C# headless: abstrair tudo (IFrameDecoder/IAudioMuxer/IMediaFileResolver/INativeBridge) com fakes; SynchronizationContext null nos testes de VM

## Eventos
- ST-20 iniciou dev (run d7919a7a) — pode estar com working tree não commitado ao retomar.
- Fix RIFE ORT: version negotiation (API 18→17 fallback) + DML probe fix + graceful degradation test.
  **Validação real (playback/GPU) = REALIZADA** — RIFE interpolação ATIVA com DirectML.
  Evidência (stderr log 2026-08-08):
  ```
  interp_rife: model loaded (rife_v4.onnx), EP=DirectML, 1920x1080, 5 frames/pair (ratio=5.400)
  interp_rife: timestep input 'timestep' resolved at index 2
  interp_rife: Process ENTER → tensors ready, inference loop N=5
  ProcessInterpolation done: count=5  ← 5 frames interpolados com sucesso
  ```
  Pipeline completa: FFmpeg decode (NV12/D3D11VA) → RIFE interp (DirectML) → FSR upscale → AMF encode.
  ⚠️ Novo bug descoberto: AMF encoder falha com DXGI_ERROR_DEVICE_REMOVED (0x887A0001) após 1° par de frames.
  Este é um bug SEPARADO (ST-16/encode ou ST-15/interop) — RIFE está 100% funcional.
  Camada C# testada com fakes: 542 testes, 0 falhas.

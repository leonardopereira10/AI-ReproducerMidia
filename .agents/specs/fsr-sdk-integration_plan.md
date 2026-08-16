# Plano de Execução — Integração FidelityFX SDK (FSR Upscale + Frame Generation)

**Sprint:** SPRINT_04
**Data:** 2026-08-13
**Doc de referência do usuário:** https://gpuopen.com/manuals/fsr_sdk/
**SDK baixado pelo usuário:** `lib/FidelityFX-Samples-v2.3.0-prebuilt/` (FidelityFX Samples v2.3.0 prebuilt)

---

## Objetivo

Integrar o AMD FidelityFX SDK (FFX API 2.x) ao CATRA, entregando:

1. **Upscale FSR 4 (ML, RDNA 4) / fallback FSR 3.1** via FFX API runtime, substituindo o
   stub atual (`CATRA_HAS_FSR4` nunca compilado) no pipeline de upscale.
2. **Frame Generation (FSR 3 FG)** no caminho de **playback**, via FFX Frame Generation
   Swapchain (DX12) com optical flow interno — interpolação de frames de vídeo para
   apresentação mais suave no player.

## Contexto Técnico (fatos verificados — base obrigatória para subtasks)

### O que o pacote do usuário contém
- `lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/`:
  - `amd_fidelityfx_loader_dx12.dll` — loader FFX API (exporta `ffxCreateContext`,
    `ffxDestroyContext`, `ffxConfigure`, `ffxQuery`, `ffxDispatch` — ABI estável verificada).
  - `amd_fidelityfx_upscaler_dx12.dll` — runtime de upscale (FSR 4 ML em RDNA 4, FSR 3.1 fallback).
  - `amd_fidelityfx_framegeneration_dx12.dll` — runtime de frame generation (com optical flow interno).
  - `amd_ags_x64.dll` — AMD AGS (suporte).
- O pacote **NÃO contém headers** — eles serão vendorados do FidelityFX-SDK v2.3.0
  (GitHub `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK`, tag v2.3.0, licença MIT):
  `Kits/FidelityFX/api/include/` (ffx_api.h/hpp, ffx_api_loader.h, ffx_api_types.h, dx12/),
  `Kits/FidelityFX/upscalers/include/` (ffx_upscale.h/hpp),
  `Kits/FidelityFX/framegeneration/include/` (ffx_framegeneration.h/hpp, dx12/).

### Padrão de integração (FFX API 2.x, oficial — `ffx_api_loader.h`)
- `LoadLibrary("amd_fidelityfx_loader_dx12.dll")` + `GetProcAddress` das 5 funções
  (struct `ffxFunctions` + `ffxLoadFunctions`). **Sem .lib, sem dependência de build-time.**
- Upscale: `ffxCreateContextDescUpscale` (create) + `ffxDispatchDescUpscale` (dispatch por frame).
  **API temporal**: exige `color`, `depth`, `motionVectors`, `jitterOffset`. Para vídeo
  (sem MV de engine) usa-se **modo vídeo**: buffer de MV zerado (RG32F, renderSize),
  depth dummy, `jitter=(0,0)`, `reset=true` em scene-cut. Qualidade menor que engine com MV —
  documentado como limitação; FSR 1 (EASU) permanece default para export.
- Frame Generation playback:
  `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12` (cria swapchain DX12 direto no HWND
  do HwndHost WPF) + `ffxCreateContextDescFrameGeneration` + `ffxConfigureDescFrameGeneration`
  (swapChain, callbacks). `frameGenerationCallback` = `ffxDispatch(fgCtx, &params->header)`.
  **Sem dispatch de Prepare (sem depth/MV de jogo) → o FG usa optical flow interno**
  (`FFX_FRAMEINTERPOLATION_PASS_OPTICAL_FLOW_VECTOR_FIELD`) — adequado para vídeo.
  Fluxo por frame: compartilhar textura D3D11→D3D12 → copy/letterbox para o FG backbuffer →
  `Present`; o swapchain interpola e paceia frames automaticamente.
- Referência oficial: `Samples/Upscalers/FidelityFX_FSR/dx12/fsrapirendermodule.cpp` (1882 linhas,
  será vendorado como referência read-only em `docs/fsr/`).

### Scaffolding existente (reutilizar)
- `native/catra-gpu/catra_gpu.h`: ABI flat C já reserva `CATRA_UPSCALE_FSR4=2` e
  `CATRA_INTERP_FSR3FG=2`.
- `native/catra-gpu/upscale_fsr4.cpp`: stub compile-gated (`CATRA_HAS_FSR4`) — será
  **substituído** por implementação runtime-loaded (build sem SDK, detecção em runtime).
- `native/catra-gpu/d3d_interop.cpp`: device D3D12 compartilhado, `ShareTexture`
  (D3D11→D3D12, keyed mutex), pool de texturas.
- `src/CATRA.Services/Playback/VideoRenderer.cs`: renderer D3D11 atual (swapchain no HWND
  do HwndHost) — caminho FG cria renderer novo que assume o HWND quando FG ativo.

## Escopo

- Vendor de headers FFX API 2.3.0 (MIT) + referência do sample em `docs/fsr/`.
- Módulo `ffx_runtime` no catra-gpu: LoadLibrary/GetProcAddress, capability probe,
  bridge de log FFX→BackendLog, version query.
- Deploy das DLLs runtime junto ao catra-gpu.dll e na saída do .NET (CMake install + csproj copy).
  **Lista completa (A1):** `amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`,
  `amd_fidelityfx_framegeneration_dx12.dll`, `amd_ags_x64.dll`, `amd_acs_x64.dll`,
  `D3D12Core.dll` (Agility), `dxcompiler.dll`, `dxil.dll` (runtime shader compile DXIL).
  O smoke test nativo deve validar carga de TODAS.
- Backend de upscale FSR 4/3.1 real via FFX API (substitui stub), modo vídeo zero-MV,
  disponível como method=2; fallback graceful se DLLs ausentes (→ FSR 1).
- Contorno do bug AMF (`docs/FSR_AMF_ISSUE_CONTEXT.md`) **na fronteira do encode, genérico**
  (A2): qualquer input D3D12 que chegue ao `catra_encode_frame` recebe cópia GPU para
  D3D11 antes do `CreateSurfaceFromDX12Native` → desbloqueia também o export FSR 1
  (perfil DLNA), que hoje está bloqueado pelo mesmo bug.
- Módulo nativo `catra_fg_*` (playback). **Superfície ABI proposta (A3):**
  - `catra_is_fg_available()` → 0/1 (loader + DLLs FG + adapter DX12).
  - `catra_fg_create(void* hwnd, int w, int h, double video_fps, int* out_ctx)`.
  - `catra_fg_present(int ctx, void* frame_texture /*ID3D11Texture2D* */, int frame_w, int frame_h)`
    — share D3D11→D3D12 + letterbox copy para o FG backbuffer + Present.
  - `catra_fg_resize(int ctx, int w, int h)` — recreate de swapchain em resize.
  - `catra_fg_destroy(int ctx)`.
  Extensão do `NativeBridge.cs` (ou bridge de playback dedicada) com os P/Invokes correspondentes.
  **Docstring de `catra_is_fsr4_available()` em `catra_gpu.h` será atualizada** para a
  semântica runtime-load (não depende mais de build com `CATRA_HAS_FSR4`).
- C#/: novo `IVideoRenderer` (FsrFrameGenRenderer) usado quando FG habilitado nas settings;
  seleção de renderer no **PlaybackEngine** (factory do IVideoRenderer); toggles de settings
  (upscale: off/FSR1/FSR4; FG on/off) + indicação de capacidade na UI.
- **Matriz upscale×FG no playback (A6):** uso simultâneo PERMITIDO — upscale é aplicado ao
  frame antes da submissão ao FG backbuffer (upscale → FG). Default: FSR1 (EASU) + FG;
  FSR4 + FG permitido, sujeito à limitação zero-MV documentada.
- Testes: unitários C# (seleção de renderer, wrappers NativeBridge, settings), smoke test
  nativo (tool de verificação de carga das DLLs/probes).
- Documentação: `docs/FSR_FFX_INTEGRATION.md` (arquitetura, limitações, modo vídeo).

## Fora de Escopo

- Correção do bug AMF `CreateSurfaceFromDX12Native` (bug aberto separado; contorno via cópia D3D11).
- Motion vectors reais para upscale temporal (optical flow externo / modelo ONNX de OF).
- FG no pipeline offline de export (FG é swapchain-bound, aplicável só a playback).
- UI composition/HUD sobre frames gerados, HDR/PQ, anti-lag 2.
- Vulkan / outras APIs. Alterações no RIFE (interpolação offline continua RIFE).

## Critérios de Aceite

- [ ] `dotnet build CATRA.sln` + build nativo passam **sem** nenhum SDK FSR em build-time
      (tudo runtime-loaded); sem as DLLs FFX em runtime, o app funciona com fallback
      (upscale FSR 1, FG indisponível) sem crash.
- [ ] Com as DLLs presentes + GPU AMD: `catra_is_fsr4_available()` retorna 1 e upscale
      method=2 produz output correto (validado por smoke test nativo e/ou QA visual).
- [ ] Pipeline offline com upscale FSR 4 entrega arquivo .mp4 válido (cópia D3D12→D3D11
      na fronteira do encode contorna o bug AMF documentado).
- [ ] **Export com upscale FSR 1 também entrega .mp4 válido** (contorno genérico desbloqueia
      o perfil DLNA) (A2).
- [ ] Playback com FG habilitado: clipe 30 fps com FG on apresenta ≥1,5× a contagem de
      frames decodificados (presents ~2×) **e** drift A/V ≤ ±40 ms (A4/A5).
- [ ] FG desabilitado → renderer D3D11 atual, comportamento inalterado.
- [ ] Falha na criação do FG swapchain → fallback automático para renderer D3D11, sem crash (A4).
- [ ] Settings: toggles de upscale (off/FSR1/FSR4) e FG on/off persistem e são aplicados.
- [ ] Testes unitários novos passam (`dotnet test`); sem regressões nos existentes.
- [ ] Documentação `docs/FSR_FFX_INTEGRATION.md` atualizada (incluindo limitação zero-MV).

## Riscos

1. **Bug AMF × texturas D3D12** (docs/FSR_AMF_ISSUE_CONTEXT.md): output FSR 4 (D3D12) não é
   aceito pelo encoder. Mitigação: cópia GPU D3D12→D3D11 (infra de interop já existe).
   Se nem a cópia funcionar, upscale FSR 4 fica restrito ao playback nesta sprint.
2. **FG em janela WPF HwndHost**: swapchain DX12 sobre child-HWND dentro de WPF pode ter
   issues de composição/resize. Mitigação: tratar resize/recreate; fallback automático
   para renderer D3D11 em falha de criação do FG swapchain.
3. **Qualidade do upscale temporal zero-MV**: ghosting em movimento/cortes. Mitigação:
   reset em scene-cut, FSR 1 permanece default para export; documentado.
4. **Version mismatch** headers 2.3.0 × DLLs do pacote samples 2.3.0: mesma origem (SDK
   2.3.0) → risco baixo; validado por query de versão no loader (fail-safe).
5. **Estado do repositório sujo** (arquivos de bugfix não commitados fora do escopo FSR):
   commits desta sprint serão apenas dos arquivos do fileScope (nunca `git add .`).
6. **Sincronia A/V com FG** (A5): o swapchain FG paceia frames interpolados e pode deslocar
   a apresentação do relógio de áudio. Mitigação: submissão de frames dirigida pelo clock de
   playback (não pelo present) + verificação de drift ≤ ±40 ms no QA.
7. **Licenciamento** (A7): headers vendorizados e DLLs redistribuídas devem manter a
   atribuição MIT (LICENSE/nota de copyright AMD) junto aos arquivos.

## Referências

- Manual oficial: https://gpuopen.com/manuals/fsr_sdk/
- FFX API loader pattern: `Kits/FidelityFX/api/include/ffx_api_loader.h` (SDK 2.3.0)
- Sample referência: `fsrapirendermodule.cpp` (FidelityFX_FSR sample dx12)
- Bug conhecido: `docs/FSR_AMF_ISSUE_CONTEXT.md`

## Aprovação PO

**APROVADO** (product-owner, 2026-08-13) com 7 ajustes — A1 (deploy DLLs completo),
A2 (contorno AMF genérico no encode), A3 (ABI catra_fg_* especificada), A4 (critério FG
mensurável + fallback), A5 (risco A/V sync), A6 (matriz upscale×FG + PlaybackEngine),
A7 (licenciamento MIT) — **todos incorporados nesta revisão**.

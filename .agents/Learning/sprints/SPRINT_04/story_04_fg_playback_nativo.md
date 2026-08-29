# Story 04 — Módulo nativo de Frame Generation para playback (catra_fg_*)

**Sprint:** SPRINT_04 · **Spec/Plano:** `.agents/specs/fsr-sdk-integration_plan.md` (aprovado 2026-08-13)
**Tipo:** dev

## Descrição

Módulo nativo `catra_fg_*` no catra-gpu que entrega Frame Generation (FSR 3 FG) no
caminho de **playback** via FFX Frame Generation Swapchain (DX12), usando o
**optical flow interno** do runtime (adequado para vídeo — sem dispatch de Prepare,
pois não há depth/MV de jogo).

Mecânica: `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12` cria o swapchain DX12
direto no HWND do HwndHost WPF → `ffxCreateContextDescFrameGeneration` →
`ffxConfigureDescFrameGeneration` (swapChain + callbacks), com
`frameGenerationCallback = ffxDispatch(fgCtx, &params->header)`. Por frame: share da
textura D3D11→D3D12 (keyed mutex, infra existente de `d3d_interop.cpp`), copy/letterbox
para o FG backbuffer e `Present`; o swapchain interpola e paceia frames automaticamente.

**Superfície ABI (A3):**
- `catra_is_fg_available()` → 0/1 (loader + DLLs FG + adapter DX12)
- `catra_fg_create(void* hwnd, int w, int h, double video_fps, int* out_ctx)`
- `catra_fg_present(int ctx, void* frame_texture /*ID3D11Texture2D* */, int frame_w, int frame_h)` — share D3D11→D3D12 + letterbox copy + Present
- `catra_fg_resize(int ctx, int w, int h)` — recreate de swapchain em resize
- `catra_fg_destroy(int ctx)`

## FileScope (preliminar)

**Criar:**
- `native/catra-gpu/catra_fg.h` — ABI `catra_fg_*` (superfície A3 acima)
- `native/catra-gpu/catra_fg.cpp` — swapchain FG, contextos, share D3D11→D3D12, letterbox copy, recreate em resize

**Modificar:**
- `native/catra-gpu/catra_gpu.h` / `catra_gpu.cpp` — export dos símbolos `catra_fg_*` e reaproveitamento do device compartilhado (ou header próprio incluído pelo catra_gpu.h — decisão de implementação)
- `native/catra-gpu/CMakeLists.txt` — inclusão do novo módulo

**NÃO tocar:** `VideoRenderer.cs`/`PlaybackEngine.cs`/`NativeBridge.cs` (consumo C# é da
Story 05), `encode_amf.cpp`, `upscale_*.cpp`.

## Critérios de Aceite

- [ ] ABI `catra_fg_*` exatamente conforme A3 (assinaturas e semântica acima), exportada por catra-gpu.dll.
- [ ] `catra_is_fg_available()` retorna 1 com DLLs FFX presentes + adapter DX12, e 0 sem elas.
- [ ] Smoke test nativo (ou extensão do existente): create em HWND de teste → N presents → destroy, sem leak/vazamento de handles; com DLLs ausentes, create falha com código de erro (sem crash).
- [ ] Submissão contínua de frames a 30 fps resulta em taxa de presents ≈ 2× (≥1,5×) — medido no teste nativo.
- [ ] `catra_fg_resize` faz recreate do swapchain sem crash e sem apresentar em tamanho errado.
- [ ] Falha de criação do swapchain FG retorna erro (não crash) — habilitando o fallback automático da Story 05 (A4).
- [ ] Build sem dependência de SDK FSR em build-time (runtime-loaded); testes existentes sem regressão.

## Dependências

- **Story 01** (headers, `ffx_runtime`, DLLs de framegeneration).
- Interop existente (`d3d_interop.cpp`) já disponível — sem nova dependência.

## Notas

- FG é **swapchain-bound** → aplicável só a playback (fora de escopo no export offline).
- Risco 2 do plano: swapchain DX12 sobre child-HWND do WPF HwndHost pode ter issues de
  composição/resize — recreate em resize + fallback (Story 05) mitigam.
- Risco 6 (A5): o swapchain paceia frames interpolados; a submissão dirigida pelo clock de
  playback é responsabilidade do consumo C# (Story 05); o módulo nativo não deve bloquear
  o caller além do present sync.

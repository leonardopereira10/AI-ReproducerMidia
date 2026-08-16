# Subtask 04: Módulo nativo `catra_fg_*` — Frame Generation no playback via FFX FG Swapchain (DX12, optical flow interno)

**Story:** story_04_fg_playback_nativo.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alta

> Justificativa da complexidade (skill complexity-eval): integração nativa C++/DX12
> multi-camadas (FG swapchain context + FG context + interop D3D11→D3D12 com keyed
> mutex + letterbox render pass), ciclo de vida com teardown/recreate em resize,
> sincronização cross-API, critério de performance mensurável (presents ≥1,5×) e
> smoke test E2E com HWND real → enquadra em "fluxos complexos + E2E + performance".

## Descrição

Módulo nativo `catra_fg_*` em `native/catra-gpu/` que entrega FSR 3 Frame Generation
no caminho de **playback**, usando o FFX Frame Generation Swapchain (DX12) criado
direto no HWND do HwndHost WPF, com **optical flow interno** do runtime (vídeo não
tem depth/MV de jogo → nenhum dispatch de Prepare).

**Decisão de arquitetura (dentro da liberdade dada pela story — "ou header próprio
incluído pelo catra_gpu.h — decisão de implementação"):** seguir o padrão existente
do bridge — a ABI flat C é **declarada** em `catra_fg.h` (incluído no fim de
`catra_gpu.h`), os **entry points + registry** vivem em `catra_gpu.cpp` (único lugar
com `GuardCabi`/`GuardCabiVoid` e acesso a `g_device`/`g_d3d12Device`), e a classe
interna `catra::FgRenderer` vive em `catra_fg.cpp`. Isso replica exatamente o padrão
upscale/RIFE/encode (backends com classe C++ própria, registry e entry points em
`catra_gpu.cpp`) e evita duplicar a barreira de exceções C ABI.

**Mecânica central (verificada nos headers vendorados + referência oficial):**

1. `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12` cria o swapchain proxy
   DX12 direto no HWND (`swapchain` = `IDXGISwapChain4**` out, `hwnd`,
   `desc` = `DXGI_SWAP_CHAIN_DESC1*`, `fullscreenDesc` = NULL (windowed),
   `dxgiFactory`, `gameQueue` = fila DIRECT), com
   `ffxCreateContextDescFrameGenerationSwapChainVersionDX12` encadeado via `pNext`
   (`version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION`) — referência:
   `lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp:1806-1850`
   (`EnableFrameInterpolationSwapchain`) + `MakeWindowAssociation(hwnd,
   DXGI_MWA_NO_WINDOW_CHANGES)` após criar (evita Alt+Enter hijack no host WPF).
2. `ffxCreateContextDescFrameGeneration` (`displaySize = {w,h}`, `maxRenderSize =
   {w,h}`, `backBufferFormat = FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM`, `flags = 0` —
   vídeo SDR, sem async) encadeado com `ffxCreateBackendDX12Desc` (`device` = device
   D3D12 do interop) e `ffxCreateContextDescFrameGenerationVersion`
   (`version = FFX_FRAMEGENERATION_VERSION`) — referência:
   `fsrapirendermodule.cpp:1100-1226` (ordem `createFg → backendDesc → headerVersion`).
3. `ffxConfigureDescFrameGeneration` com `swapChain` = swapchain FG,
   `frameGenerationCallback` = callback estático que faz
   `ffxDispatch(*((ffxContext*)userCtx), &params->header)` (padrão exato do sample,
   `fsrapirendermodule.cpp:1204-1219`), `presentCallback = NULL` em produção,
   `frameGenerationEnabled = true`, `HUDLessColor` vazia, `generationRect =
   {0,0,w,h}` (área total; as barras letterbox são estáticas/pretas — interpolar a
   área cheia é correto e evita re-configure a cada mudança de aspect), `frameID`
   inicial — referência: `fsrapirendermodule.cpp:1198-1224`.
4. **SEM dispatch de Prepare** (`ffxDispatchDescFrameGenerationPrepare`/`V2` exigem
   depth/motionVectors de jogo — o runtime cai para o optical flow interno, plano
   "Frame Generation playback"). frameID deve incrementar exatamente +1 por frame
   apresentado (qualquer outro delta reseta a lógica FG — doc do campo em
   `ffx_framegeneration.h`).
5. Por frame (`catra_fg_present`): share D3D11→D3D12 (infra existente) + letterbox
   do frame no backbuffer FG + `Present(1, 0)`; o swapchain interpola e paceia os
   frames gerados na taxa do display. A submissão é dirigida pelo clock do caller
   (Story 05); o módulo nativo só bloqueia no present sync (risco 6 do plano).
6. `catra_fg_resize`: teardown (configure `frameGenerationEnabled=false` →
   `ffxDestroyContext` FG → `ffxDestroyContext` swapchain → release do
   `IDXGISwapChain4`) + recreate no novo tamanho — a ordem do teardown é a do sample
   (`fsrapirendermodule.cpp:1232-1254` + `RestoreApplicationSwapChain:1765-1804`); o
   `Configure(enabled=false)` chama `waitForPresents()` internamente e evita o erro
   #921 `OBJECT_DELETED_WHILE_STILL_IN_USE` do debug layer (comentário do sample).

### Fatos verificados no código (base obrigatória)

**Headers vendorados (`lib/FidelityFX-SDK-2.3.0/`, MIT, já presentes):**
- `framegeneration/include/ffx_framegeneration.h`:
  - `ffxCreateContextDescFrameGeneration` (type
    `FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION`): `flags`, `displaySize`,
    `maxRenderSize`, `backBufferFormat`.
  - `ffxConfigureDescFrameGeneration` (type `FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION`):
    `swapChain`, `presentCallback` (`FfxApiPresentCallbackFunc`),
    `presentCallbackUserContext`, `frameGenerationCallback`
    (`FfxApiFrameGenerationDispatchFunc`), `frameGenerationCallbackUserContext`,
    `frameGenerationEnabled`, `allowAsyncWorkloads`, `HUDLessColor`, `flags`,
    `onlyPresentGenerated`, `generationRect`, `frameID`.
  - `ffxCallbackDescFrameGenerationPresent` (recebido pelo presentCallback):
    `currentBackBuffer`, `isGeneratedFrame`, `frameID` — permite contar presents
    reais (renderizados + gerados) no smoke test.
  - `FFX_FRAMEGENERATION_VERSION` (macro de versão para o desc de versão).
- `framegeneration/include/dx12/ffx_api_framegeneration_dx12.h`:
  - `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12` (type
    `FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12`):
    `swapchain (IDXGISwapChain4**)`, `hwnd`, `desc (DXGI_SWAP_CHAIN_DESC1*)`,
    `fullscreenDesc` (NULL ok p/ windowed), `dxgiFactory`, `gameQueue`.
  - `ffxCreateContextDescFrameGenerationSwapChainVersionDX12`
    (`version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION`).
  - `ffxConfigureDescFrameGenerationSwapChainKeyValueDX12` +
    `FFX_API_CONFIGURE_FG_SWAPCHAIN_KEY_FRAMEPACINGTUNING` (=2) para
    `FfxApiSwapchainFramePacingTuning` (`ffx_framegeneration_api_types.h:65-72`:
    `safetyMarginInMs`, `varianceFactor`, `allowHybridSpin`, `hybridSpinTime`,
    `allowWaitForSingleObjectOnFence`) — **não mexer nesta sprint** (defaults do
    runtime); documentado como tuning futuro.
- `api/include/ffx_api.h`: flat C `ffxCreateContext(ffxContext*, descHeader*, memCb)` /
  `ffxDestroyContext` / `ffxConfigure` / `ffxDispatch`; `ffxContext = void*`;
  `FFX_API_RETURN_OK = 0`.
- `api/include/dx12/ffx_api_dx12.h`: `ffxCreateBackendDX12Desc` (type
  `FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12`, campo `device`),
  `ffxApiGetResourceDX12(ID3D12Resource*, state, additionalUsages)` (linha 204).
- `api/include/ffx_api_types.h:45`: `FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM`;
  `FFX_API_RESOURCE_STATE_PRESENT` / `_PIXEL_COMPUTE_READ` / `_RENDER_TARGET`.

**Referência oficial (`lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp`, read-only):**
- Criação do FG swapchain ForHwnd: linhas ~1806-1850 (desc + version desc via pNext +
  `CreateContext` + release do factory após create + `MakeWindowAssociation`).
- Criação/config do FG context: linhas ~1100-1226 (incl. callback lambda linhas 1204-1219).
- Teardown: linhas ~1232-1254 (configure disabled antes de destroy) e
  `RestoreApplicationSwapChain` linhas ~1765-1804 (disciplina de refcount do proxy:
  refcount deve zerar ao destruir o swapchain context).
- frameID: `m_FrameID += 1` por frame (linha 1663).

**Infra do bridge (NÃO modificar):**
- `d3d_interop.h/.cpp`: `interop_init` já cria o device D3D12 **e a fila DIRECT**
  (exigência do FG swapchain: `gameQueue` DIRECT) no mesmo adapter do D3D11;
  `interop_share_d3d11_to_d3d12(src, &tex12, &ntHandle)` — zero-copy ou pooled copy
  com keyed mutex; **o caller deve `CloseHandle(ntHandle)` após o share** (doc do
  header); protocolo ping-pong do keyed mutex (`interop_acquire`/`interop_release`,
  timeout 5000 ms) + resync por `interop_pool_generation()` (consumer reseta a key
  para 0 quando a geração muda — o consumer half já existe em `Fsr4Upscaler::Process`,
  replicar o padrão `KeyedMutexGuard` RAII).
- `catra_gpu.cpp`: `GuardCabi`/`GuardCabiVoid` (linhas 96-135), `log_msg`,
  `catra::BackendLog` (linha 145), registry pattern com handles densos
  (`g_upscaleContexts` linhas 183-215 — copiar o padrão Register/Lookup/Destroy/
  DestroyAll), `g_device`/`g_deviceContext`/`g_d3d12Device`/`g_d3d12Queue`,
  `catra_shutdown` destrói os registries ANTES de `interop_shutdown` (ordem já
  documentada lá — FG entra na mesma ordem).
- `CMakeLists.txt`: target `catra-gpu` com `/W4 /WX /permissive- /utf-8`; padrão de
  tool diagnóstico `catra-interop-test` (compila os sources do bridge no exe).
- `ffx_runtime` da **subtask_01** (contrato real, já escrito):
  `catra::ffx::FfxRuntime::Load() / IsLoaded() / Functions() (const ffxFunctions&) /
  ProbeDependencyDlls() (as 8 DLLs A1, incl. amd_fidelityfx_framegeneration_dx12.dll
  e amd_ags_x64.dll) / ProbeDx12Adapter() / IsAvailable()` + include dirs FFX já
  configurados no CMake + variáveis `CATRA_FFX_SDK_HEADERS` / `CATRA_FFX_DLL_DIR` /
  `CATRA_FFX_DLLS`.

### Mapeamento de edge cases (todos identificados)

1. `catra_init` não chamado (sem device D3D11/DX12) → `CATRA_ERR_INIT` em
   create/present/resize.
2. Runtime FFX ausente (`FfxRuntime::IsAvailable()` falso — loader/DLLs/adapter) →
   `catra_is_fg_available()` == 0; `catra_fg_create` → `CATRA_ERR_NOT_IMPL`, sem crash.
3. Interop soft-fail (sem `g_d3d12Device`, ex.: sem adapter DX12) → create →
   `CATRA_ERR_DEVICE`; `catra_is_fg_available()` == 0.
4. HWND inválido (`IsWindow(hwnd)` falso) ou w/h ≤ 0 ou video_fps ≤ 0 ou out_ctx
   NULL → `CATRA_ERR_INVALID_ARG`.
5. Falha em `ffxCreateContext` (swapchain ou FG) ou `ffxConfigure` → `CATRA_ERR_DEVICE`
   + log com o return code FFX; nada parcialmente vivo vaza (teardown do que foi
   criado antes de retornar erro) — é o gancho do fallback automático da Story 05.
6. `frame_w/frame_h` ≤ 0 ou textura NULL em present → `CATRA_ERR_INVALID_ARG`.
7. Frame com resolução ≠ backbuffer → letterbox/scale render pass (qualquer direção,
   upscale ou downscale); igual → fast path copy.
8. Keyed-mutex timeout (5 s) no share → `CATRA_ERR_DEVICE` (sem deadlock; protocolo
   ping-pong + resync de pool generation já provado no FSR 4).
9. Present falha (`DXGI_ERROR_DEVICE_REMOVED/RESET/HUNG` — log
   `GetDeviceRemovedReason`) → `CATRA_ERR_DEVICE`, contexto marcado morto; presents/
   resize seguintes retornam `CATRA_ERR_DEVICE`; destroy continua seguro.
10. Resize para o mesmo tamanho → no-op `CATRA_OK`; resize com w/h ≤ 0 →
    `CATRA_ERR_INVALID_ARG`; resize falha no recreate → contexto morto (`CATRA_ERR_DEVICE`
    daí em diante), destroy ainda funciona.
11. Destroy com handle desconhecido / duplo destroy → no-op (padrão dos outros registries).
12. Storm de resize (várias chamadas rápidas) → nativo tolera chamadas sequenciais;
    debounce é responsabilidade do caller (Story 05) — documentado no header.
13. `catra_shutdown` com contexto FG vivo → `DestroyAllFg()` antes de
    `interop_shutdown`/release dos devices (mesma posição dos outros registries).
14. Concorrência: UM produtor por contexto (a thread de playback); callbacks FFX
    (frameGeneration/present) rodam em threads do runtime → nenhum lock do bridge
    pode ser segurado dentro deles; observer de teste usa `std::atomic`. Nenhuma
    exceção C++ atravessa a ABI (GuardCabi) nem os callbacks (try/catch interno →
    return `FFX_API_RETURN_ERROR`).
15. `CloseHandle` do NT handle retornado por `interop_share_d3d11_to_d3d12` em todo
    exit path (incl. falha posterior).
16. Refcount do proxy swapchain deve zerar no destroy (assert/log como no sample
    `RestoreApplicationSwapChain`) — vazar ref = janela preta/zumbi no HWND.

## Arquivos Alvo (fileScope)

**Criar (3):**
- `native/catra-gpu/catra_fg.h` — declaração da ABI A3 (5 funções `CATRA_API`,
  docstrings completas com ownership/threading/error codes) + declaração da classe
  interna `catra::FgRenderer` (interface só com tipos primitivos/`void*` — nada de
  headers COM no .h; membros em pimpl no .cpp). O arquivo USA o macro `CATRA_API`
  definido por `catra_gpu.h` e é incluído no fim dele (sem include-guard circular).
- `native/catra-gpu/catra_fg.cpp` — implementação de `FgRenderer`: criação do FG
  swapchain (ForHwnd) + FG context + configure, frame path (share + letterbox +
  Present), resize (teardown + recreate), destroy, callbacks estáticos FFX, HLSL do
  letterbox compilado via `D3DCompile` (precedente: `nv12_to_bgra_shader.cpp` e o
  EASU de `upscale_fsr1.cpp`).
- `native/catra-gpu/tools/fg_smoke_test.cpp` — smoke test E2E nativo (detalhado no
  passo 7).

**Modificar (3):**
- `native/catra-gpu/catra_gpu.h` — `#include "catra_fg.h"` no fim (após a definição
  de `CATRA_API` e dos codes `CATRA_ERR_*`); atualização do bloco STATUS citando o
  módulo FG (runtime-loaded, playback-only). Nenhuma assinatura existente muda.
- `native/catra-gpu/catra_gpu.cpp` — os 5 entry points `catra_fg_*` dentro de
  `GuardCabi`/`GuardCabiVoid` + registry `g_fgContexts` (padrão `g_upscaleContexts`:
  `RegisterFg/LookupFg/DestroyFg/DestroyAllFg`, handles densos, mutex próprio) +
  hook interno `catra::FgSetPresentObserver(int ctx, cb, user)` usado APENAS pelo
  smoke test (não exportado) + `DestroyAllFg()` em `catra_shutdown` junto dos outros
  registries (antes de `interop_shutdown`).
- `native/catra-gpu/CMakeLists.txt` — `catra_fg.cpp` na lista do `add_library(catra-gpu
  SHARED ...)`; target `catra-fg-smoke-test` (padrão `catra-interop-test`: compila os
  sources do bridge no exe) com POST_BUILD copy das 8 DLLs FFX reutilizando
  `CATRA_FFX_DLL_DIR`/`CATRA_FFX_DLLS` da subtask_01 (guardado por
  `if(DEFINED CATRA_FFX_DLLS)`); nenhum `.lib` FFX novo.

**NÃO tocar:** `VideoRenderer.cs` / `PlaybackEngine.cs` / `NativeBridge.cs` (consumo
C# = Story 05), `encode_amf.*`, `upscale_*` (Story 02), `d3d_interop.*` (consumidor
apenas), `ffx_runtime.*` (subtask_01), headers em `lib/FidelityFX-SDK-2.3.0/`
(read-only). Commits apenas deste fileScope (**nunca `git add .`** — repo sujo fora
do escopo).

## Passos

### 1. `catra_fg.h` — ABI A3 + classe interna

Declarar (docstrings obrigatórias, mesmas convenções de `catra_gpu.h`):

```c
CATRA_API int  catra_is_fg_available(void);
CATRA_API int  catra_fg_create(void* hwnd, int w, int h, double video_fps, int* out_ctx);
CATRA_API int  catra_fg_present(int ctx, void* frame_texture, int frame_w, int frame_h);
CATRA_API int  catra_fg_resize(int ctx, int w, int h);
CATRA_API void catra_fg_destroy(int ctx);
```

Contratos a documentar no header:
- `catra_is_fg_available()`: 1 sse `catra_init` rodou com interop DX12 vivo E
  `FfxRuntime::IsAvailable()` (loader + 8 DLLs A1 + adapter DX12). Nunca bloqueia.
- `catra_fg_create`: `hwnd` precisa ser janela válida (`IsWindow`); `w/h` = tamanho
  do viewport de apresentação; `video_fps` é informativa (usada em log/diagnóstico e
  pelo smoke test para a expectativa de razão — o pacing dos frames interpolados é do
  swapchain FG, dirigido pela taxa do display); cria o FG swapchain no HWND (este
  contexto ASSUME o HWND — o renderer D3D11 atual não deve apresentar na mesma
  janela enquanto FG vive). Erros: `CATRA_ERR_INIT`, `CATRA_ERR_INVALID_ARG`,
  `CATRA_ERR_NOT_IMPL` (runtime FFX ausente), `CATRA_ERR_DEVICE` (falha de
  swapchain/contexto). Threading: não thread-safe por contexto; um produtor.
- `catra_fg_present`: `frame_texture` = `ID3D11Texture2D*` BGRA (ownership do
  caller, zero-copy read — nunca liberado pelo módulo); faz share D3D11→D3D12 +
  letterbox no backbuffer FG + `Present(1,0)`. Pode bloquear até ~1 frame de display
  (throttle/backpressure do swapchain) — NÃO assumir non-blocking; a cadência quem
  dá é o clock de playback do caller. Erros: `CATRA_ERR_CONTEXT`,
  `CATRA_ERR_INVALID_ARG`, `CATRA_ERR_DEVICE` (incl. device removed — contexto
  marcado morto; fallback é do caller, Story 05).
- `catra_fg_resize`: recreate completo (teardown + novo swapchain) no mesmo HWND;
  não apresentar em tamanho errado é garantido (backbuffers novos nascem w×h).
  Mesma dimensão = no-op. Debounce de storm de resize é do caller.
- `catra_fg_destroy`: idempotente p/ handle desconhecido; seguro após contexto morto.

Classe interna (sem headers COM no .h):

```cpp
namespace catra {
class FgRenderer {
public:
    static int Create(void* hwnd, int w, int h, double video_fps, FgRenderer** out);
    int  Present(void* d3d11_texture, int frame_w, int frame_h);
    int  Resize(int w, int h);
    void SetPresentObserver(void (*cb)(bool is_generated_frame, void* user), void* user); // teste
    ~FgRenderer(); // teardown completo, idempotente
private:
    struct Impl; Impl* m_impl; // pimpl no .cpp
};
void FgSetPresentObserver(int ctx, void (*cb)(bool, void*), void* user); // hook em catra_gpu.cpp
} // namespace catra
```

### 2. `catra_fg.cpp` — Impl/estado

`struct FgRenderer::Impl` (tudo RAII, `ComPtr`; nenhuma exceção escapa dos
callbacks/entry points):
- Borrowed do bridge: `ID3D11Device*`, `ID3D12Device*`, `ID3D12CommandQueue*` (a
  fila DIRECT do interop — exigência do `gameQueue`), obtidos via os mesmos caminhos
  que `catra_gpu.cpp` usa (device12/queue12 de `interop_init`; expor acesso via
  helper existente `catra::CreateD3D12Device` para o device; para a fila, adicionar
  getter mínimo se necessário — sem mudar comportamento de `d3d_interop`).
- Próprios: `IDXGIFactory` (do create), `ffxContext swapchainCtx`, `ComPtr<IDXGISwapChain4>
  fgSwapchain`, `ffxContext fgCtx`, `DXGI_SWAP_CHAIN_DESC1 desc1` (guardada no ctx —
  ver nota de lifetime de `ffx_api.h`), HWND, w/h, `videoFps`, `uint64_t frameID = 0`,
  flag `dead`, fence/allocator/cmdList próprios para o render pass (espera com
  timeout 10 s — nunca `INFINITE`), 2 RTVs (um por backbuffer), PSO/root
  signature/sampler do letterbox, consumer key do keyed mutex + última
  `interop_pool_generation()` vista, observer atômico.

### 3. `FgRenderer::Create` — swapchain + contexto FG

Ordem exata (espelha o sample):
1. `FfxRuntime::Load()` (idempotente) + `IsAvailable()` falso → `CATRA_ERR_NOT_IMPL`.
2. Validar args; device12/fila ausentes → `CATRA_ERR_DEVICE`.
3. `CreateDXGIFactory1(IID_PPV_ARGS(&factory))`.
4. `DXGI_SWAP_CHAIN_DESC1 desc1{}`: `Width=w`, `Height=h`,
   `Format=DXGI_FORMAT_B8G8R8A8_UNORM`, `Stereo=FALSE`, `SampleDesc={1,0}`,
   `BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT`, `BufferCount=2`,
   `Scaling=DXGI_SCALING_NONE`, `SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD`,
   `AlphaMode=DXGI_ALPHA_MODE_IGNORE`, `Flags=0`.
5. `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 sc{}` +
   `ffxCreateContextDescFrameGenerationSwapChainVersionDX12 scVer{ .version =
   FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION }` encadeada
   (`sc.header.pNext = &scVer.header`); `sc.gameQueue = fila DIRECT`;
   `sc.fullscreenDesc = nullptr` (windowed); `sc.swapchain = &fgSwapchainOut`.
   `FfxRuntime::Functions().CreateContext(&swapchainCtx, &sc.header, nullptr)`;
   falhou → log do código + cleanup do factory → `CATRA_ERR_DEVICE` (critério de
   aceite: erro, não crash).
6. `factory->MakeWindowAssociation((HWND)hwnd, DXGI_MWA_NO_WINDOW_CHANGES)`.
7. FG context: `ffxCreateContextDescFrameGeneration fg{}` (`displaySize=maxRenderSize=
   {w,h}`, `backBufferFormat=FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM`, `flags=0`) →
   pNext → `ffxCreateBackendDX12Desc backend{ .device = device12 }` → pNext →
   `ffxCreateContextDescFrameGenerationVersion fgVer{ .version =
   FFX_FRAMEGENERATION_VERSION }`. `CreateContext(&fgCtx, &fg.header, nullptr)`;
   falhou → teardown do swapchain criado + `CATRA_ERR_DEVICE`.
8. Configure: `ffxConfigureDescFrameGeneration cfg{}`:
   - `swapChain = fgSwapchain.Get()`;
   - `frameGenerationCallback = &FgDispatchTrampoline` →
     `return FfxRuntime::Functions().Dispatch(*static_cast<ffxContext*>(user), &params->header);`
     (padrão do sample 1204-1219; try/catch interno → `FFX_API_RETURN_ERROR`);
     `frameGenerationCallbackUserContext = &impl->fgCtx`;
   - `presentCallback = PresentTrampoline` (NULL-observado: se sem observer instalado,
     repassa vazio — manter o trampoline SEMPRE, pois é também o ponto de contagem do
     smoke test; custo desprezível) — trampoline thread-safe, sem locks do bridge;
   - `frameGenerationEnabled=true`, `allowAsyncWorkloads=false`, `HUDLessColor=FfxApiResource{}`,
     `flags=0`, `onlyPresentGenerated=false`, `generationRect={0,0,w,h}`, `frameID=0`.
   `Functions().Configure(fgCtx, &cfg.header)`; falhou → teardown completo → `CATRA_ERR_DEVICE`.
9. Criar RTVs dos 2 backbuffers (`fgSwapchain->GetBuffer`), compilar o HLSL letterbox
   (passo 5) + PSO/root signature; falha → teardown completo → `CATRA_ERR_DEVICE`.
10. Log de sucesso com w×h, video_fps e versões FFX.

### 4. `Present` — frame path

Ordem exata:
1. Contexto morto / handle inválido / args inválidos → erros do header.
2. `interop_share_d3d11_to_d3d12(src11, &tex12, &ntHandle)` → falhou: `CATRA_ERR_DEVICE`;
   sucesso: `CloseHandle(ntHandle)` (todo exit path).
3. Keyed mutex consumer half (padrão `Fsr4Upscaler::Process`): geração do pool mudou
   (`interop_pool_generation()`) → key=0; `QI IDXGIKeyedMutex` em `tex12`;
   `interop_acquire(mtx, key, 5000)` → timeout: `CATRA_ERR_DEVICE`; guard RAII faz
   `interop_release(mtx, key)` + `key ^= 1` em todo exit path.
4. Esperar a fence do próprio frame anterior (timeout 10 s) antes de regravar
   allocator/list.
5. `allocator->Reset()` + `cmdList->Reset()`; barriers: backbuffer atual
   (`GetCurrentBackBufferIndex`) `PRESENT → RENDER_TARGET`; `tex12` `COMMON →
   PIXEL_SHADER_RESOURCE`.
6. **Fast path** `frame_w==w && frame_h==h`: `CopyTextureRegion` (dst = backbuffer
   subresource 0, src = tex12 subresource 0). **Senão** letterbox PS pass: rect
   inteiro com aspect preservado (`scale = min(w/frame_w, h/frame_h)`;
   `dstW/H = frame*scale`; centro), CB com src/dst dims, `DrawInstanced(3,1,0,0)`
   fullscreen triangle.
7. Barriers de saída: `tex12 → COMMON` (libera o keyed mutex limpo), backbuffer
   `RENDER_TARGET → PRESENT`; `Close` + `ExecuteCommandLists` na fila DIRECT.
8. `fgSwapchain->Present(1, 0)`; `HRESULT` de erro (`DEVICE_REMOVED/RESET/HUNG`) →
   log `GetDeviceRemovedReason()`, contexto `dead=true`, `CATRA_ERR_DEVICE`.
9. `Signal(fence)`; `frameID += 1` (exatamente +1 — doc do campo); `ReleaseSync`
   via guard.

### 5. Letterbox render pass (HLSL + PSO)

- HLSL embutido em string no .cpp, compilado com `D3DCompile` (`vs_5_0`/`ps_5_0`) em
  `Create` — mesmo padrão de `nv12_to_bgra_shader.cpp`. VS fullscreen-triangle por
  `SV_VertexId`; PS: sample bilinear do SRV (static sampler linear clamp), calcula o
  rect letterbox via CB (`srcW,srcH,dstW,dstH,padX,padY`) e emite preto fora do rect.
- Root signature: 1 SRV (table) + CBV root entry; heap `CBV_SRV_UAV` de 1 slot.
- Precedente de fallback: se `D3DCompile` falhar → `CATRA_ERR_DEVICE` no create
  (caller cai no fallback da Story 05).

### 6. `Resize` e teardown

- `Resize(w,h)`: mesmo tamanho → `CATRA_OK`. Senão: (a) esperar GPU idle (fence);
  (b) `Configure(fgCtx, frameGenerationEnabled=false)` (flush interno de presents —
  evita #921 OBJECT_DELETED_WHILE_STILL_IN_USE, comentário do sample 1237-1240);
  (c) `DestroyContext(&fgCtx)`; (d) `DestroyContext(&swapchainCtx)`;
  (e) `fgSwapchain.Release()` — log do refcount residual (deve zerar, padrão
  `RestoreApplicationSwapChain` 1784-1791); (f) solar RTVs/PSO; (g) recreate com a
  MESMA sequência do passo 3 no novo tamanho (`frameID` continua — sem gap, sem reset;
  `cfg.frameID = frameID` atual). Falha no recreate → `dead=true` + `CATRA_ERR_DEVICE`.
- `~FgRenderer`: mesma sequência (b)-(f), tolerando runtime já descarregado
  (`FfxRuntime::IsLoaded()` falso → pular destroy dos contextos FFX e só soltar COM).

### 7. `catra_gpu.cpp` + `catra_gpu.h` — wiring

- Registry espelhando `g_upscaleContexts` (linhas 183-215): `g_fgMutex`,
  `g_fgContexts`, `g_nextFgHandle`, `RegisterFg/LookupFg/DestroyFg/DestroyAllFg`.
- Entry points: `catra_is_fg_available` = `GuardCabi` → `g_initialized &&
  g_d3d12Device && catra::ffx::FfxRuntime::IsAvailable() ? 1 : 0`; create/present/
  resize em `GuardCabi` (lookup sob o mutex, trabalho fora do lock); destroy em
  `GuardCabiVoid`.
- `catra_shutdown`: `DestroyAllFg()` junto dos outros registries (antes de
  `interop_shutdown`).
- `catra_gpu.h`: `#include "catra_fg.h"` no fim + linha no bloco STATUS.
- Hook de teste `catra::FgSetPresentObserver(int, cb, user)` (não exportado):
  lookup + `SetPresentObserver`.

### 8. `CMakeLists.txt`

- `catra_fg.cpp` no `add_library(catra-gpu SHARED ...)` (lista atual + 1 entrada).
- Target do smoke (não instalado, padrão `catra-interop-test`):
  ```cmake
  add_executable(catra-fg-smoke-test
      tools/fg_smoke_test.cpp
      catra_gpu.cpp catra_fg.cpp ffx_runtime.cpp d3d_interop.cpp encode_amf.cpp
      interp_rife.cpp nv12_to_bgra_shader.cpp upscale_fsr1.cpp upscale_fsr4.cpp)
  ```
  mesmos includes/defines/links do `catra-gpu` (FFX include dirs da subtask_01,
  `CATRA_HAS_AMF`/ONNX condicionais, `d3d12 dxgi d3d11 dxguid d3dcompiler`) +
  POST_BUILD copy das 8 DLLs (`CATRA_FFX_DLL_DIR/${_dll}` → exe dir, guardado por
  `if(DEFINED CATRA_FFX_DLLS)`); `/W3 /utf-8 /EHsc` como `catra-interop-test`.
  (Compilar os sources no exe — em vez de linkar o implib do DLL — mantém o teste
  independente da ordem de install, igual ao tool existente.)

### 9. `tools/fg_smoke_test.cpp`

1. `RegisterClassExW` + `CreateWindowExW` 640×360 (windowed; pump leve com
   `PeekMessage` no loop).
2. `D3D11CreateDevice` (adapter default, `D3D11_CREATE_DEVICE_BGRA_SUPPORT`) +
   `catra_init(device)`.
3. Se `catra_is_fg_available() == 0` → imprimir motivo e `return 0` (skip gracioso —
   máquina sem DLLs/GPU compatível não quebra CI). Com flag `--expect-unavailable`:
   exigir available==0 E `catra_fg_create` retornando código de erro (sem crash) →
   valida o critério de aceite de DLLs ausentes (rodar manualmente do diretório sem
   as DLLs, como o teste de ausência da subtask_01).
4. Disponível: `catra_fg_create(hwnd, 640, 360, 30.0, &ctx) == CATRA_OK`; instalar
   observer via `catra::FgSetPresentObserver` (contadores `std::atomic<uint64_t>`
   total/gerados).
5. Submeter 90 frames (3 s a 30 fps; textura BGRA gradiente criada via D3D11,
   `Sleep(33)` entre presents; pump de mensagens a cada iteração).
6. Asserts: todos os presents `CATRA_OK`; `apresentados(observer) >= 1,5 × 90`
   (imprimir razão e taxa de frames gerados; requisito: display ≥ ~45 Hz — imprimir
   também o total para diagnóstico). Sem observer → sem medição: falhar com mensagem.
7. `catra_fg_resize(ctx, 800, 450) == CATRA_OK` + 10 presents 800×450 OK;
   `catra_fg_resize` mesmo tamanho = no-op; `catra_fg_present` com args inválidos →
   `CATRA_ERR_INVALID_ARG`.
8. `catra_fg_destroy(ctx)`; repetir create/destroy 3× (churn sem leak de handles GDI:
   snapshot `GetGuiResources(GR_GDIOBJECTS)` antes/depois, delta ≤ tolerância pequena);
   destroy de handle desconhecido = no-op; `catra_shutdown`; imprimir `PASS`, `return 0`.

### 10. Validação (build-gate)

1. `pwsh ./scripts/build-native.ps1 -Configuration Release` → build verde com `/W4 /WX`.
2. `dumpbin /exports install/runtimes/win-x64/native/catra-gpu.dll | findstr catra_fg_`
   → os 5 símbolos exportados (critério "exportada por catra-gpu.dll").
3. Rodar `build/.../catra-fg-smoke-test.exe` → `PASS` (com GPU/DLLs disponíveis);
   teste de ausência com `--expect-unavailable` → erro sem crash.
4. `dotnet build CATRA.sln` (sem regressão; nenhum C# tocado) + `dotnet test`.
5. `catra-interop-test` (regressão do tool existente).

## Critérios de Aceite

- [ ] ABI `catra_fg_*` exatamente conforme A3 (5 assinaturas da story), exportada por
      `catra-gpu.dll` (verificado via `dumpbin /exports`).
- [ ] `catra_is_fg_available()` == 1 com runtime FFX completo + adapter DX12 +
      `catra_init`; == 0 sem eles (e `catra_fg_create` retorna erro, sem crash).
- [ ] Smoke test nativo: create em HWND real → 90 presents a 30 fps → destroy,
      3 ciclos de churn sem vazamento de handles (GDI estável) e sem erro nos presents.
- [ ] Presents medidos pelo observer ≥ 1,5× os frames submetidos (esperado ≈ 2× em
      display 60 Hz) — razão impressa no output do smoke.
- [ ] `catra_fg_resize` faz teardown+recreate sem crash e os presents seguintes usam
      o tamanho novo (RTVs/backbuffers recriados).
- [ ] Falha de criação do swapchain/contexto FG retorna `CATRA_ERR_DEVICE` com todo o
      estado parcial limpo (base do fallback automático da Story 05) — nenhum throw
      atravessa a ABI (GuardCabi + trampoline com try/catch).
- [ ] Nenhum dispatch de Prepare no caminho FG (optical flow interno); frameID
      incrementando exatamente +1 por present.
- [ ] Present usa pacing dirigido pelo clock do caller: o módulo só bloqueia no
      `Present(1,0)` sync; nenhuma espera interna adicional (fence waits apenas de
      reciclagem de command objects, com timeout).
- [ ] Build nativo + `dotnet build CATRA.sln` passam sem nenhum `.lib` FFX linkado
      (runtime-loaded via `ffx_runtime`); keyed-mutex consumer half preservado
      (acquire/release em todo exit path + resync de pool generation + `CloseHandle`
      do NT handle).
- [ ] `dotnet test` + `catra-interop-test` sem regressão.
- [ ] Commit contém APENAS arquivos do fileScope (nunca `git add .`).

## Dependências

- **subtask_01 (Story 01) — ffx_runtime + infra: BLOQUEANTE.** Consome
  `catra::ffx::FfxRuntime` (Load/IsAvailable/Functions), os include dirs FFX no
  CMake e as variáveis `CATRA_FFX_DLL_DIR`/`CATRA_FFX_DLLS` (deploy das 8 DLLs —
  `amd_fidelityfx_framegeneration_dx12.dll` e `amd_ags_x64.dll` inclusas). Se a API
  real diferir do contrato acima, adaptar as chamadas (não reimplementar o loader).
- **d3d_interop (ST-15)** — já implementado, NÃO modificar: device D3D12 + fila
  DIRECT, `interop_share_d3d11_to_d3d12`, `interop_acquire/release`,
  `interop_pool_generation`.
- **subtask_02 (Story 02)** — NÃO é dependência (módulos independentes); apenas
  referência de padrão (consumer half do keyed mutex em `Fsr4Upscaler::Process`).
- Headers FG vendorados: `lib/FidelityFX-SDK-2.3.0/framegeneration/include/`
  (`ffx_framegeneration.h`, `dx12/ffx_api_framegeneration_dx12.h`,
  `ffx_framegeneration_api_types.h`) — read-only.
- Consumidores futuros (NÃO são dependência): Story 05 (P/Invoke no
  `NativeBridge`/bridge de playback, `FsrFrameGenRenderer`, fallback automático em
  falha, submissão dirigida pelo clock + debounce de resize) e Story 06 (QA do
  critério A/V drift ≤ ±40 ms e da razão de presents no app real).

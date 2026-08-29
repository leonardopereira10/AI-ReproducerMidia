# Subtask 02: Backend FSR 4/3.1 nativo via FFX API 2.x runtime-loaded (modo vídeo zero-MV)

**Story:** story_02_upscale_fsr4_nativo.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alta

## Descrição

Substituir o stub compile-gated de FSR 4 (`native/catra-gpu/upscale_fsr4.cpp`, ramo
`CATRA_HAS_FSR4` — hoje nunca compilado) por uma implementação **real, sempre compilada e
runtime-loaded** via FFX API 2.x flat C, usando o módulo `ffx_runtime` da subtask_01
(`LoadLibrary("amd_fidelityfx_loader_dx12.dll")` + `GetProcAddress` das 5 funções —
padrão `ffx_api_loader.h` / struct `ffxFunctions`). **Sem .lib, sem dependência de
build-time**: apenas headers vendorados (já presentes em `lib/FidelityFX-SDK-2.3.0/`).

Modo vídeo (zero-MV): a API de upscale FFX é temporal e exige `color`, `depth`,
`motionVectors`, `jitterOffset`. Sem MV de engine, o backend alimenta MV zerado
(RG32F, renderSize), depth dummy (R32F, renderSize), `jitter=(0,0)` e
`reset=true` no primeiro frame e em scene-cut (sinalizado pelo caller via novo entry
point `catra_upscale_reset`). Limitação de qualidade (ghosting) é documentada; FSR 1
(EASU) permanece default de export — `upscale_fsr1.cpp` NÃO é tocado.

**Nota de decisão (desvio justificado do fileScope preliminar da story):** a story limita
`catra_gpu.h` a "atualização de docstring", mas o critério de aceite "reset=true em
scene-cut" é impossível sem um canal para o caller sinalizar o corte — a ABI atual não tem
nenhum. Esta subtask adiciona **um** entry point novo (`catra_upscale_reset`), a menor
extensão possível. Alternativa rejeitada: reset todo frame (desabilitaria a acumulação
temporal do FSR 4 e degradaria exatamente o que o modo temporal oferece). Reviewer: confirmar.

### Fatos verificados no código (base obrigatória)

- Headers vendorados (MIT, SDK 2.3.0) já existem:
  - `lib/FidelityFX-SDK-2.3.0/api/include/ffx_api.h` (5 entry points flat C:
    `ffxCreateContext/ffxDestroyContext/ffxConfigure/ffxQuery/ffxDispatch`; return codes
    `FFX_API_RETURN_*`; `ffxApiMessage` = `void(*)(uint32_t type, const wchar_t* msg)`).
  - `lib/FidelityFX-SDK-2.3.0/api/include/ffx_api_loader.h` (`ffxFunctions`, `ffxLoadFunctions`).
  - `lib/FidelityFX-SDK-2.3.0/api/include/ffx_api_types.h` (`FfxApiResource`,
    `FfxApiDimensions2D`, `FfxApiFloatCoords2D`, `FFX_API_RESOURCE_STATE_*`).
  - `lib/FidelityFX-SDK-2.3.0/api/include/dx12/ffx_api_dx12.h`
    (`ffxCreateBackendDX12Desc` type `FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12`;
    helper C++ `ffxApiGetResourceDX12(ID3D12Resource*, state, additionalUsages)` que
    deriva usage do desc D3D12 — **UAV usage só é inferido se o resource tiver
    `D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS`**).
  - `lib/FidelityFX-SDK-2.3.0/upscalers/include/ffx_upscale.h`
    (`ffxCreateContextDescUpscale` — campos: `flags`, `maxRenderSize`, `maxUpscaleSize`,
    `fpMessage`; **NÃO tem qualityMode** — preset vira escolha de resolução.
    `ffxDispatchDescUpscale` completo. `ffxCreateContextDescUpscaleVersion`
    type `FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION` com `version =
    FFX_UPSCALER_VERSION`. `FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE` etc.).
- Referência oficial de uso: `lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp`
  (read-only): create com cadeia `createFsr → backendDesc → headerVersion`
  (linhas ~948-1040), dispatch completo (linhas ~1433-1492: `jitterOffset`,
  `motionVectorScale = renderSize`, `reset`, `frameTimeDelta` em **ms**, `preExposure`,
  `renderSize`/`upscaleSize`, camera params), destroy via `ffxDestroyContext` (linha ~1254).
- Infra a reutilizar (NÃO modificar): `d3d_interop.h/.cpp` — `CreateD3D12Device` (retorna
  o device interop compartilhado após `interop_init`), `ShareTexture` (D3D11→D3D12, pooled
  copy + keyed mutex), `interop_acquire`/`interop_release`/`interop_pool_generation`
  (protocolo ping-pong 0/1 + resync em pool-rebuild — o stub atual já implementa o
  consumer half corretamente em `Process`; manter o padrão `KeyedMutexGuard` RAII).
- Dispatch atual em `catra_gpu.cpp`: `catra_upscale_create` só roda o probe
  `Fsr4IsAvailable(g_device.Get())` quando `method==2`, depois
  `ResolveUpscaleMethod` (em `upscale_fsr1.h` — puro, intocado) faz downgrade 2→1.
  `catra_upscale_process` roteia `c->fsr4->Process(src,&dst12)`. Manter esse wiring.
- `Fsr4IsCompiled()` não tem nenhum consumidor fora de `upscale_fsr4.{h,cpp}` (grep
  verificado) → remover junto com o conceito de compile-gate.

### Contrato assumido com subtask_01 (`ffx_runtime`)

subtask_01 ainda não foi escrita; esta subtask consome o módulo com o contrato mínimo
abaixo (derivado do escopo da Story 01: "LoadLibrary/GetProcAddress, capability probe,
bridge de log FFX→BackendLog, version query"). Se os nomes reais diferirem, o
implementador adapta as chamadas — a ordenação garante que `ffx_runtime` existe antes:

- `bool catra::FfxRuntimeIsLoaded()` — loader DLL carregado + 5 ponteiros válidos +
  `amd_fidelityfx_upscaler_dx12.dll` presente.
- `const ffxFunctions* catra::FfxFunctionsGet()` — ponteiros carregados (nunca null se
  `FfxRuntimeIsLoaded()`).
- Adapter de mensagem FFX→`BackendLog` (ou sink `ffxApiMessage` reutilizável).

## Arquivos Alvo (fileScope)

**Modificar (5):**
1. `native/catra-gpu/upscale_fsr4.h` — doc nova (runtime-load), remover `Fsr4IsCompiled()`,
   manter `Fsr4IsAvailable(ID3D11Device*)` e `Fsr4Upscaler` (mesmas assinaturas de
   `Create`/`Process`), adicionar `int RequestSceneCut()` (seta flag `pendingReset`).
2. `native/catra-gpu/upscale_fsr4.cpp` — reescrita completa: remover os dois ramos
   (`#if CATRA_HAS_FSR4` C++ wrapper / stub inerte) e implementar backend flat C
   runtime-loaded (passos 2-8).
3. `native/catra-gpu/catra_gpu.h` — docstring de `catra_is_fsr4_available()` (semântica
   runtime-load: loader + DLLs FFX presentes + adapter compatível; não depende mais de
   build com `CATRA_HAS_FSR4`); novo entry point `catra_upscale_reset(int ctx)`;
   atualizar bloco STATUS e docstring de `catra_upscale_create` (method=2 = FFX runtime,
   não mais SDK de build-time).
4. `native/catra-gpu/catra_gpu.cpp` — implementar `catra_upscale_reset` (lookup do
   contexto; method==FSR4 → `fsr4->RequestSceneCut()`; fsr1/passthrough → no-op;
   handle desconhecido → `CATRA_ERR_CONTEXT`; tudo dentro de `GuardCabi`); comentários do
   probe (sem mudança de assinatura de `Fsr4IsAvailable`).
5. `native/catra-gpu/CMakeLists.txt` — remover bloco `CATRA_WITH_FSR4` /
   `CATRA_FSR_SDK_ROOT` / `CATRA_FSR_SDK_LIB` (compile-gate morto); adicionar include dirs
   `${CMAKE_SOURCE_DIR}/../../lib/FidelityFX-SDK-2.3.0/api/include` e
   `.../upscalers/include` ao target `catra-gpu` (headers-only, nenhuma lib linkada);
   nenhuma mudança de link (runtime-load). Fontes do `ffx_runtime` entram no target via
   subtask_01.

**Modificar (1, smoke — opcional se o smoke da subtask_01 já cobrir):**
6. `native/catra-gpu/tools/interop_readback_test.cpp` — modo `--fsr4-smoke`: skip
   gracioso quando `catra_is_fsr4_available()==0`; senão create 320x180→640x360 method=2,
   process de textura gradiente, readback e check de dimensões/output não-corrompido.

**Criar:** nada (texturas dummy MV/depth e heap UAV são internos ao `Impl`).

**NÃO tocar:** `upscale_fsr1.cpp/.h` (fallback/default de export), `d3d_interop.cpp/.h`
(consumidor apenas), `encode_amf.*` (Story 03), código C# (Story 05), headers em
`lib/FidelityFX-SDK-2.3.0/` (read-only, vendorados na Story 01).

## Passos

1. **CMake (passo 5 do fileScope):** remover o bloco `--- FSR 4 / FidelityFX SDK (ST-14)`
   inteiro (option `CATRA_WITH_FSR4`, detecção `CATRA_FSR_SDK_ROOT`, link de
   `CATRA_FSR_SDK_LIB`) e substituir por include dirs dos headers vendorados + comentário
   "runtime-loaded via ffx_runtime (Story 01); headers-only, no link-time dependency".
   Verificar que o build configura sem nenhuma flag `-DCATRA_FSR_SDK_ROOT`.

2. **`upscale_fsr4.h`:** apagar `Fsr4IsCompiled()` e a documentação de compile-gate.
   Manter `bool Fsr4IsAvailable(ID3D11Device*)` com doc nova: "loader FFX + DLLs
   presentes (via ffx_runtime) + probe de contexto throw-away no device D3D12 do adapter
   succeeds". Adicionar em `Fsr4Upscaler`: `int RequestSceneCut();` (threading: mesma
   regra do `Process` — não thread-safe por instância).

3. **`upscale_fsr4.cpp` — Impl/estado:** reescrever `struct Fsr4Upscaler::Impl` com:
   device/queue/allocator/cmdList/fence/fenceEvent (como hoje), `ffxContext upscaleCtx =
   nullptr` (handle opaco da flat C API, **não** mais `ffx::ContextUpscale`), texturas
   dummy persistentes `mvTex` (DXGI_FORMAT_R32G32_FLOAT, srcW×srcH) e `depthTex`
   (DXGI_FORMAT_R32_FLOAT, srcW×srcH) ambas `D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS`,
   estado inicial `D3D12_RESOURCE_STATE_UNORDERED_ACCESS`, optimized clear 0; heap UAV
   `CBV_SRV_UAV` shader-visible com 2 slots + `CreateUnorderedAccessView` das duas;
   flag `dummiesZeroed=false`; `pendingReset=true`; consumer key do keyed-mutex
   (`consumerKey`, `lastPoolGeneration` — manter lógica existente).

4. **`Fsr4IsAvailable` (nova semântica):**
   (a) `catra::FfxRuntimeIsLoaded()` falso → log INFO + return false;
   (b) **remover** o pré-filtro RDNA 4 (`LooksLikeRdna4`/device-ID 0x7550-0x75FF) — o
   runtime FFX decide FSR 4 ML (RDNA 4) vs fallback FSR 3.1 no adapter; manter check de
   adapter apenas via probe;
   (c) probe definitivo: `CreateD3D12Device` (device interop) → create throw-away de
   64×64→128×128 (mesmo caminho de `Create`, desc do passo 5) → destroy → true.
   Qualquer falha → false + log. Nunca lança.

5. **`Fsr4Upscaler::Create`:** validar args (manter); `CreateD3D12Device`; criar command
   plumbing (manter o do stub). Montar a cadeia de create descriptors (locais, lifetime
   até o retorno de `CreateContext` — ver sample linhas 948-1040):
   ```
   ffxCreateContextDescUpscale desc{};
   desc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
   desc.flags = FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE; // input BGRA é gamma-encoded (vídeo)
   desc.maxRenderSize = {srcW, srcH};
   desc.maxUpscaleSize = {dstW, dstH};
   desc.fpMessage = <sink ffx_runtime FFX→BackendLog, ou local wchar→UTF-8>;
   ffxCreateBackendDX12Desc backend{}; backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12; backend.device = device12;
   ffxCreateContextDescUpscaleVersion ver{}; ver.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION; ver.version = FFX_UPSCALER_VERSION;
   desc.header.pNext = &backend.header; backend.header.pNext = &ver.header;
   rc = FfxFunctionsGet()->CreateContext(&ctx, &desc.header, nullptr);
   ```
   `rc != FFX_API_RETURN_OK` → `CATRA_ERR_DEVICE` + log com o código. **Não usar**
   `ffxOverrideVersion`/`GetVersions` (sem seleção de provider nesta sprint). O quality
   mode (`SelectQualityMode`, calculado em `catra_gpu.cpp`) não tem campo na desc nova —
   manter apenas para log/diagnóstico (comentar isso; não tentar validar ratio).

6. **`Process` — frame path (zero-MV):** ordem exata:
   1. `ShareTexture(d3d11Device, device12, src, &srcRes)` (manter) +
      `KeyedMutexGuard` com resync de pool-generation (manter código atual:
      `interop_pool_generation()` mudou → `consumerKey=0`; `interop_acquire(mtx,key,5000)`;
      release no destrutor em **todo** exit path).
   2. `allocator->Reset()` + `cmdList->Reset()`.
   3. Se `!dummiesZeroed`: gravar no cmdList barrier → UAV, `ClearUnorderedAccessViewFloat`
      (0.0f) em `mvTex` e `depthTex`, barrier de volta; `dummiesZeroed=true`.
   4. Preencher `ffxDispatchDescUpscale d{}` (valores concretos):
      - `header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE`;
      - `commandList = cmdList.Get()` (void* direto — flat C, sem helper);
      - `color = ffxApiGetResourceDX12(srcRes.Get(), FFX_API_RESOURCE_STATE_COMMON)`
        (resource aberto por NT handle inicia em COMMON; o runtime FFX grava as barriers);
      - `depth = ffxApiGetResourceDX12(depthTex.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS)`;
      - `motionVectors = ffxApiGetResourceDX12(mvTex.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS)`;
      - `exposure / reactive / transparencyAndComposition = FfxApiResource{}` (null);
      - `output = ffxApiGetResourceDX12(dstRes.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS)`
        — `dstRes` criado **por frame** (contrato atual: caller recebe textura nova
        AddRef'd) dstW×dstH `B8G8R8A8_UNORM` (BGRA — decisão D-PO-4: alinha com FSR1 e
        `AMF_SURFACE_BGRA`; o smoke `--fsr4-smoke` valida o formato) com `ALLOW_UNORDERED_ACCESS`, estado inicial
        UAV (o usage UAV é inferido da flag — obrigatório, ver `ffxApiGetResourceDX12`);
      - `jitterOffset = {0.f, 0.f}`; `motionVectorScale = {(float)srcW, (float)srcH}`;
      - `renderSize = {srcW, srcH}`; `upscaleSize = {dstW, dstH}`;
      - `enableSharpening=false; sharpness=0.f` (tunável em QA, manter default off);
      - `frameTimeDelta = 16.6667f` (dummy ms; sem fps no backend — comentar);
      - `preExposure = 1.0f`; `cameraNear = 0.1f; cameraFar = 1000.f;
        cameraFovAngleVertical = 1.0472f; viewSpaceToMetersFactor = 1.f`
        (dummies documentados — vídeo não tem câmera);
      - `reset = pendingReset` (consome: `pendingReset=false` após gravar);
      - `flags = 0`.
   5. `FfxFunctionsGet()->Dispatch(ctx, &d.header)` → falha: log + `CATRA_ERR_DEVICE`
      (cmdList pode ficar em estado indeterminado → descartar gravação com Reset no
      próximo frame; não executar).
   6. `cmdList->Close()`, `ExecuteCommandLists`, `Signal(fence)`, espera **com timeout de
      10 s** (`WaitForSingleObject(fenceEvent, 10000)`; substituir o `INFINITE` do stub;
      timeout → `CATRA_ERR_DEVICE`).

7. **`RequestSceneCut`:** `pendingReset = true; return CATRA_OK;` (sem GPU work; o
   próximo `Process` leva `reset=true`).

8. **Destructor:** `DestroyContext(&ctx, nullptr)` via `ffxFunctions` (apenas se ctx !=
   nullptr e runtime ainda carregado), release de mvTex/depthTex/heap/command objects,
   `CloseHandle(fenceEvent)`.

9. **`catra_gpu.h`:** docstring nova de `catra_is_fsr4_available()` (semântica
   runtime-load); declarar e documentar `catra_upscale_reset(int ctx)` (marca scene-cut →
   próximo dispatch FSR 4 usa `reset=true`; no-op em fsr1/passthrough; `CATRA_OK` /
   `CATRA_ERR_CONTEXT`); atualizar STATUS e docstring de `catra_upscale_create`.

10. **`catra_gpu.cpp`:** implementar `catra_upscale_reset` (`GuardCabi`, lookup sob
    `g_upscaleMutex`, `method==CATRA_UPSCALE_FSR4 → c->fsr4->RequestSceneCut()`);
    atualizar comentários do probe. Nenhuma outra mudança no dispatch existente.

11. **Smoke nativo (arquivo 6):** `--fsr4-smoke` no `interop_readback_test` conforme
    fileScope; deve PASSAR com skip gracioso (exit 0 + mensagem) quando as DLLs FFX não
    estão presentes.

12. **Build gate:** build nativo completo
    (`scripts/build-native.ps1` ou cmake configure+build+install do catra-gpu) sem nenhuma
    flag de SDK; `dotnet build CATRA.sln`; `dotnet test`; rodar o
    `catra-interop-test` (regressão) e o `--fsr4-smoke` (se hardware/DLLs disponíveis).

## Critérios de Aceite

- [ ] Build nativo + `dotnet build CATRA.sln` passam **sem** SDK FSR em build-time
      (headers-only, zero .lib); nenhuma referência restante a `CATRA_HAS_FSR4` /
      `CATRA_FSR_SDK_ROOT` fora de comentários históricos.
- [ ] Com DLLs FFX presentes + GPU compatível: `catra_is_fsr4_available()` == 1 e
      `catra_upscale_create(method=2)` + N×`catra_upscale_process` produzem output
      dstW×dstH sem corruption (smoke `--fsr4-smoke` e/ou QA visual).
- [ ] Sem DLLs FFX: `catra_is_fsr4_available()` == 0; `catra_upscale_create(method=2)`
      cai para FSR 1 (`catra_get_upscale_mode()` == 1) sem crash — caminho já existente
      de `ResolveUpscaleMethod`, agora alimentado pelo probe runtime.
- [ ] Confirmado no código: MV zerado (RG32F renderSize, clear 0), depth dummy (R32F),
      `jitterOffset=(0,0)`, `reset=true` no primeiro frame e após `catra_upscale_reset`.
- [ ] Keyed-mutex consumer half preservado (acquire antes do dispatch, release em todo
      exit path, resync de `interop_pool_generation`).
- [ ] `ffxDestroyContext` chamado no destrutor e no probe throw-away; fence wait com
      timeout (sem `INFINITE`).
- [ ] `catra-interop-test` existente sem regressão; testes/smoke existentes passam.

## Dependências

- **subtask_01 (Story 01) — ffx_runtime: BLOQUEANTE.** Esta subtask consome
  `FfxRuntimeIsLoaded()`/`ffxFunctions` carregados + sink de log + headers vendorados em
  `lib/FidelityFX-SDK-2.3.0/` + deploy das DLLs (`amd_fidelityfx_loader_dx12.dll`,
  `amd_fidelityfx_upscaler_dx12.dll`, etc.). Se a API real do `ffx_runtime` diferir do
  contrato assumido acima, adaptar as chamadas (não reimplementar o loader).
- **d3d_interop (ST-15)** — já implementado; consumidor apenas (`CreateD3D12Device`,
  `ShareTexture`, `interop_acquire/release/pool_generation`).
- **upscale_fsr1 (ST-14)** — fallback intocado; `ResolveUpscaleMethod`/`SelectQualityMode`
  permanecem lá.
- Consumidores futuros (NÃO são dependência desta subtask): Story 03 (encode usará o
  output D3D12 via cópia D3D12→D3D11) e Story 05 (C# chamará `catra_upscale_reset` em
  fronteira de episódio/cena).

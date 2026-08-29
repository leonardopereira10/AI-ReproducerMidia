# Subtask 03: Contorno AMF genérico no encode (cópia GPU D3D12→D3D11) + upscale FSR 4 offline com fallback

**Story:** story_03_contorno_amf_encode_offline.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alta

> Justificativa da complexidade (skill complexity-eval): fluxo multi-camada
> nativo↔C# (cópia cross-API D3D12→D3D11 com NT handles, keyed mutex e pool de
> texturas + integração/fallback no pipeline de export), E2E em hardware real
> (encode AMF + validação de .mp4), edge cases de sincronização GPU (fence,
> pool-rebuild resync, estados de resource) e implicações de performance
> (custo por-frame no caminho de encode).

## Descrição

Duas entregas acopladas (story 03, ajuste A2 do plano aprovado):

1. **Contorno GENÉRICO do bug AMF na fronteira do encode.** Bug documentado em
   `docs/FSR_AMF_ISSUE_CONTEXT.md`: `AMFContext2::CreateSurfaceFromDX12Native`
   retorna `AMF_FAIL` (res=1) para texturas D3D12 **criadas por usuário**
   (output de upscale FSR 1/FSR 4 — BGRA, heap shared, flags corretas, mesmo
   device). Fato verificado que sustenta o contorno: o caminho D3D11 de
   `catra_encode_frame` já funciona hoje — texturas D3D12 **abertas pelo pool
   de interop** a partir de texturas D3D11 shared (NT handle) SÃO aceitas pelo
   AMF. O contorno: **todo** input D3D12 que chega a `catra_encode_frame` é
   copiado na GPU para o caminho D3D11 do interop (pool) e o AMF passa a
   receber o resource D3D12 do slot do pool. Genérico = nenhum branch por
   origem (FSR 1/FSR 4/outro) — apenas o probe D3D12 vs D3D11 já existente.
   Desbloqueia o export com upscale FSR 1 (perfil DLNA), hoje bloqueado pelo
   mesmo bug. A correção do bug AMF em si permanece fora de escopo (bug aberto
   separado).

2. **Integração offline do upscale FSR 4 (method=2) com fallback graceful.**
   O pipeline de export passa a poder usar FSR 4 (backend da subtask_02) com
   fallback para FSR 1 quando as DLLs FFX estiverem ausentes (1ª linha:
   downgrade nativo já existente em `catra_upscale_create`; 2ª linha: retry C#
   em `ProcessingPipeline`). **FSR 1 (EASU) permanece default para export**
   (limitação zero-MV documentada — plano, Risco 3 / A6).

**Plano de contingência (Risco 1 do plano):** se a cópia D3D12→D3D11 NÃO
funcionar no encoder em hardware real (pool texture rejeitada pelo AMF, ou
falha/hang na cópia), o escopo é reduzido: FSR 4 fica restrito ao playback
nesta sprint, o contorno é revertido/gateado e o fato é registrado
explicitamente no reporte + nota de status em `docs/FSR_AMF_ISSUE_CONTEXT.md`.
Gatilho: falha reproduzida na RX 9070 XT com log do smoke `--encode-d3d12-workaround`
e/ou do export real.

### Fatos verificados no código (base obrigatória)

- **Fronteira do encode** (`native/catra-gpu/catra_gpu.cpp`, `catra_encode_frame`,
  ~linhas 856-980): probed via QI `ID3D12Resource`; se D3D12 → **zero-copy direto**
  para `AmfEncoder::Encode` (é aí que o bug mordе — log "texture is D3D12
  (zero-copy)"); se D3D11 → `interop_share_d3d11_to_d3d12` (pooled copy, caminho
  que funciona). Cleanup RAII `ShareCleanup` fecha NT handle + solta ref extra
  do D3D12 em todo exit path (padrão a manter).
- **Consumidor AMF** (`native/catra-gpu/encode_amf.cpp`): `Encode` faz QI de
  `IDXGIKeyedMutex` no resource recebido e executa o **consumer half** do
  ping-pong (`interop_acquire`/`interop_release` + resync por
  `interop_pool_generation()`) — já cobre slots do pool (caminho D3D11 atual).
  `Create` inicializa o encoder com `AMF_SURFACE_BGRA`. Já existe um fallback
  interno (AllocSurface DX12 + CopyResource em queue temporária) — manter como
  2ª linha de defesa, sem alteração.
- **Interop** (`native/catra-gpu/d3d_interop.cpp/.h`):
  - `interop_share_d3d12_to_d3d11(src, d3d11_device, out)` (~linhas 1266-1326):
    `CreateSharedHandle` no D3D12 + `OpenSharedResource1` no D3D11 — **já
    existe**, exige heap shared no src; retorna `ID3D11Texture2D` com keyed mutex.
  - `interop_share_d3d11_to_d3d12` (~linhas 1108-1264): branch zero-copy quando
    o src D3D11 é NT-shareable (cuidado — ver passo 2), senão **cópia pooled**
    GPU-GPU: `EnsurePoolLocked` → `CopyResource(slot.tex11, src)` → `Flush` →
    `WaitForD3D11GpuIdle` → `ReleaseSync(g_frameKey)` → entrega `slot.res12`
    AddRef'd, `*out_shared_handle = nullptr` (o pool mantém os handles).
    Fallback CPU round-trip via `g_pool12` quando GPU-GPU falha / NV12.
  - Pool: slots `SHARED_KEYEDMUTEX | SHARED_NTHANDLE`, rebuild só por mudança de
    geometria (inclusive formato), `g_poolGeneration` publica rebuild,
    `g_frameKey` alterna 0/1. Infra de fila D3D12 DIRECT + fence já existe
    (`g_queue`/`g_fence`/`g_fenceValue`/`g_fenceEvent`), assim como o device
    D3D12 compartilhado (`g_d3d12Device`) e o device/context D3D11 retidos.
- **Pipeline C#** (`src/CATRA.Services/Processing/ProcessingPipeline.cs`):
  `MapUpscaleMethod` ("fsr1"→1, default→2), `CreateUpscaler` sem try/catch
  (linhas ~227-233), padrão de graceful degradation de interpolação
  (try/catch `NativeBridgeException` + skip) como referência — **não** copiável
  ipsis litteris para upscale (ver passo 7). `EncodeSingle` libera a textura de
  upscale logo após `EncodeFrame` (finally) — contrato preservado pelo contorno
  (cópia síncrona).
- **Defaults de export:** `ProcessingQueueService.cs:560`
  (`_settings.Get("upscale_method") ?? "fsr4"`), `AppSettingsModel.cs:57`
  (`UpscaleMethod = "fsr4"`), teste `tests/CATRA.Data.Tests/RepositoryTests.cs:123`
  (`"fsr4"`). Story/plano exigem FSR 1 como default de export.
- **Downgrade nativo já existe:** `catra_upscale_create` (catra_gpu.cpp ~544-556)
  roda `Fsr4IsAvailable` só quando method=2 e faz `ResolveUpscaleMethod` 2→1 com
  log "FSR 4 unavailable -> downgrade to FSR 1" (documentado em
  `INativeBridge.cs:92-98`). Após a subtask_02, `Fsr4IsAvailable` vira probe do
  runtime FFX (DLLs ausentes → false).
- **Formatos:** FSR 1 produz `DXGI_FORMAT_B8G8R8A8_UNORM` (`upscale_fsr1.cpp:345`);
  o stub FSR 4 atual usa `R8G8B8A8_UNORM` (`upscale_fsr4.cpp:383`) e o rascunho
  da subtask_02 também especifica `R8G8B8A8_UNORM` — **risco de contrato**, ver
  Dependências. O encoder AMF é iniciado com `AMF_SURFACE_BGRA` e o issue doc
  registra que RGBA foi rejeitado nos testes do bug.
- **Smoke tool:** `native/catra-gpu/tools/interop_readback_test.cpp` já compila
  com `encode_amf.cpp` no target `catra-interop-test` (CMakeLists ~331-353) e já
  exercita `AmfEncoder::Create/Encode` via pool (modos de encode existentes,
  ~linhas 317-413); `CATRA_HAS_AMF` é ligado no tool quando `-DCATRA_AMF_ROOT`
  está presente (CMakeLists ~446-468).

## Arquivos Alvo (fileScope)

> Desvio justificado do fileScope preliminar da story: a story apontava
> `encode_amf.cpp/.h` como local da cópia; a fronteira real de decisão
> D3D12/D3D11 é `catra_encode_frame` em `catra_gpu.cpp`, e a cópia pertence ao
> módulo de interop. `encode_amf.cpp/.h` NÃO precisa de mudança funcional
> (consumer keyed-mutex + fallback interno já cobrem o novo input).

**Modificar (nativo):**
1. `native/catra-gpu/d3d_interop.h` — declarar `interop_copy_d3d12_for_encode`
   + documentação de contrato.
2. `native/catra-gpu/d3d_interop.cpp` — implementar o helper; extrair o
   segmento de cópia pooled BGRA para função interna reutilizável (refatoração
   sem mudança de comportamento); staging D3D12 cacheado para input não-shareable.
3. `native/catra-gpu/catra_gpu.cpp` — ramo D3D12 de `catra_encode_frame`:
   trocar zero-copy pelo helper (genérico, qualquer input D3D12); logs;
   comentário da seção. `ShareCleanup` e ramo D3D11 intactos.
4. `native/catra-gpu/tools/interop_readback_test.cpp` — novo modo
   `--encode-d3d12-workaround` (smoke E2E do contorno; skip gracioso sem AMF).

**Modificar (C#):**
5. `src/CATRA.Services/Processing/ProcessingPipeline.cs` — fallback FSR4→FSR1
   no `CreateUpscaler` (retry em `NativeBridgeException` quando method=2).
6. `src/CATRA.Services/Processing/ProcessingQueueService.cs` — default de
   export `?? "fsr4"` → `?? "fsr1"` (linha ~560).
7. `src/CATRA.Core/Models/AppSettingsModel.cs` — default `UpscaleMethod`
8. `src/CATRA.Data/Database/DatabaseInitializer.cs` — seed `("upscale_method", "fsr4")` →
   `"fsr1"` (ajuste obrigatório D-PO-3: sem alterar o seed INSERT OR IGNORE, o fallback
   nunca é atingido em instalações novas)
   `"fsr4"` → `"fsr1"` (linha ~57), alinhado ao plano (Risco 3 + A6: FSR1 é o
   default de export E de playback).
8. `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs` — 2 testes
   novos + hooks no `FakeNativeBridge`.
9. `tests/CATRA.Data.Tests/RepositoryTests.cs` — atualizar assert de seed
   `"fsr4"` → `"fsr1"` (linha ~123).

**Verificar apenas (sem mudança funcional):**
- `native/catra-gpu/encode_amf.cpp/.h` — consumer keyed-mutex, pool-generation
  resync e fallback AllocSurface-DX12 já atendem o novo input; NÃO modificar.

**NÃO tocar:** `upscale_fsr1.*`, `upscale_fsr4.*` (subtask_02), `interp_rife.*`,
`ffx_runtime.*` (subtask_01), pipeline de Casting/Library, `CATRA.UI` (story 05),
`bugfix_*.json`. Commits apenas deste fileScope (repo tem arquivos sujos fora do
escopo — **nunca `git add .`**).

## Passos

### Parte A — contorno genérico no nativo

**1. `d3d_interop.h` — declaração + contrato.** Adicionar, junto às declarações
do bloco ST-15:

```cpp
// AMF workaround (docs/FSR_AMF_ISSUE_CONTEXT.md): qualquer resource D3D12
// criado por usuário (output de upscale FSR 1/FSR 4) é rejeitado por
// AMFContext2::CreateSurfaceFromDX12Native, mas os resources D3D12 dos slots
// do pool de interop (abertos de texturas D3D11 shared via NT handle) são
// aceitos. Este helper copia o conteúdo de `src` para o caminho pooled D3D11
// e entrega o resource D3D12 do slot — contrato de saída IDÊNTICO ao de
// interop_share_d3d11_to_d3d12 (resource AddRef'd; *out_shared_handle fica
// null — o pool mantém os handles; caller fecha/solta via RAII existente).
// CONTRATO DE ENTRADA: `src` deve chegar GPU-complete (produtor fez fence
// wait) e em D3D12_RESOURCE_STATE_COMMON. Requer interop_init.
int interop_copy_d3d12_for_encode(ID3D12Resource* src,
                                  ID3D12Resource** out_pool_tex,
                                  HANDLE* out_shared_handle);
```

**2. `d3d_interop.cpp` — refatoração sem mudança de comportamento.** Extrair o
segmento GPU-GPU BGRA hoje embutido em `interop_share_d3d11_to_d3d12`
(`EnsurePoolLocked` → `CopyResource(slot.tex11, src)` → `Flush` →
`WaitForD3D11GpuIdle` → `ReleaseSync(g_frameKey)` → toggle da key só no sucesso
→ `*out = slot.res12.Get(); AddRef`) para função interna:

```cpp
// Caller segura g_mutex. Retorna CATRA_OK ou CATRA_ERR_DEVICE (falha de
// EnsurePool / ReleaseSync) — o caller histórico traduz falha em fallback CPU.
static int PooledCopyD3D11Locked(ID3D11Texture2D* src,
                                 const D3D11_TEXTURE2D_DESC& desc,
                                 ID3D12Resource** out_d3d12_tex);
```

`interop_share_d3d11_to_d3d12` passa a chamá-la (comportamento 100% idêntico,
inclusive o fallback CPU round-trip quando ela falha). **Não alterar** a branch
zero-copy nem o path NV12/CPU.

> **Armadilha registrada:** NÃO dá para reaproveitar `interop_share_d3d11_to_d3d12`
> direto na perna D3D11→D3D12 do contorno: a textura D3D11 aberta de um share
> D3D12 reporta `SHARED_KEYEDMUTEX|SHARED_NTHANDLE` no desc e cairia na branch
> **zero-copy**, reabrindo no D3D12 a MESMA alocação que o AMF rejeita. A cópia
> pooled é obrigatória — por isso a extração acima.

**3. `d3d_interop.cpp` — implementação de `interop_copy_d3d12_for_encode`.**
Tudo sob `g_mutex` (mesmo lock do módulo). Sequência:

1. Validar args (`src`, `out_pool_tex`); zerar outs; `g_d3d11Device` /
   `g_d3d11Context` / `g_d3d12Device` ausentes → `CATRA_ERR_INIT`.
2. `D3D12_RESOURCE_DESC d12 = src->GetDesc()`: exigir
   `DIMENSION_TEXTURE2D`, `MipLevels==1`, `DepthOrArraySize==1`,
   `SampleDesc.Count==1` → senão `CATRA_ERR_INVALID_ARG` (defensivo; outputs de
   upscale sempre obedecem).
3. **Probe de shareabilidade:** `g_d3d12Device->CreateSharedHandle(src, nullptr,
   DXGI_SHARED_RESOURCE_READ|WRITE, nullptr, &h)`.
   - Sucesso → `shareableSrc = src`, guarda `h`.
   - Falha (heap não-shared — caso GENÉRICO) → **staging D3D12**: textura
     cacheada `g_d3d12EncodeStaging` (recriada se a geometria/format mudar;
     `D3D12_HEAP_FLAG_SHARED`, estado inicial COMMON, sem flags de bind).
     Gravar na command list do módulo: barrier `src` COMMON→COPY_SOURCE,
     staging COMMON→COPY_DEST; `CopyResource`; barriers de volta para COMMON;
     `ExecuteCommandLists` na `g_queue`; `Signal` + espera **com timeout de
     5 s** (`WaitForSingleObject(g_fenceEvent, 5000)`; NUNCA `INFINITE`) —
     timeout → `CATRA_ERR_DEVICE`. `shareableSrc = staging`.
4. **Abrir no D3D11:** `interop_share_d3d12_to_d3d11(shareableSrc,
   g_d3d11Device.Get(), view11.ReleaseAndGetAddressOf())` (função existente);
   `CloseHandle(h)` em seguida (open consumiu; a view segura a alocação).
   Falha → `CATRA_ERR_DEVICE` + log com HRESULT.
   *Sincronização:* segura — o produtor D3D12 já fez fence wait (backends FSR
   fence-wait antes de retornar; o staging fence-wait no passo 3). A view aberta
   inicia em COMMON no lado D3D11.
5. **Cópia pooled:** `view11->GetDesc(&desc11)` →
   `PooledCopyD3D11Locked(view11, desc11, out_pool_tex)`.
   - Sucesso → `*out_shared_handle = nullptr`; log INFO do caminho tomado
     ("shareable direct" vs "staging copy"); `CATRA_OK`.
   - Falha → `CATRA_ERR_DEVICE` + log (fail-fast documentado: não adicionar
     CPU round-trip neste path nesta sprint — o contorno é o plano A; se
     falhar em hardware, aplica-se o plano de contingência, não um segundo
     contorno).
6. Todo exit path sem leak: handles fechados, views ComPtr (RAII), staging
   cacheado liberado apenas em `interop_shutdown`.

**Edge cases mapeados:** input não-shareable (staging); input shareable (open
direto); geometria inválida (INVALID_ARG); interop não inicializado (ERR_INIT);
fence timeout (ERR_DEVICE, sem hang); mudança de formato/resolução entre frames
(pool rebuild + generation bump → resync do consumer já existe em
`AmfEncoder::Encode`); falha de ReleaseSync (ERR_DEVICE); `interop_shutdown`
libera o staging.

**4. `catra_gpu.cpp` — `catra_encode_frame`.** No ramo `SUCCEEDED(qhr)`
(input D3D12), substituir o zero-copy por:

```cpp
// AMF workaround (docs/FSR_AMF_ISSUE_CONTEXT.md): TODO input D3D12 é copiado
// na GPU via pool D3D11 do interop — o AMF aceita o resource do slot, não a
// textura criada pelo usuário. Genérico: sem branch por backend de origem.
irc = catra::interop_copy_d3d12_for_encode(probed12, &d3d12res, &sharedHandle);
if (probed12 != nullptr) { probed12->Release(); } // ref do QI é nossa
log_msg(CATRA_LOG_INFO,
        "catra_encode_frame: texture is D3D12 -> D3D11 copy workaround, texture=%p",
        texture);
```

- Tratar `irc != CATRA_OK || d3d12res == nullptr` no mesmo bloco de erro já
  existente (log + cleanup defensivo + retorno do código).
- `ShareCleanup` permanece como está (handle normalmente null → CloseHandle
  é skip; solta a ref extra do pool slot após Encode).
- Ramo D3D11 (else) intocado. Atualizar o comentário da seção que fala em
  "zero-copy to AMF".
- `encode_amf.cpp/.h`: nenhuma mudança (verificar apenas que o consumer
  keyed-mutex + generation resync cobrem o slot — cobrem, caminho D3D11 atual
  já entrega slots do pool).

**5. Smoke nativo (`tools/interop_readback_test.cpp`).** Modo novo
`--encode-d3d12-workaround` (seguir o padrão dos modos de encode existentes,
que já usam `AmfEncoder::Create(g_d12.Get(), ...)` + `Encode` + dump):

1. `catra::AmfIsCompiled()` falso → imprimir `SKIP (no AMF)`, return 0.
2. Setup existente do tool (device D3D11 + `interop_init`).
3. Criar encoder pequeno (ex.: 320×180 @ 25 fps, 5000 kbps).
4. Para cada variante — **(a)** textura D3D12 BGRA com
   `D3D12_HEAP_FLAG_SHARED` e **(b)** com heap flags NONE (força o staging):
   criar, pintar padrão (barriers → estado COMMON, fence wait), chamar
   `interop_copy_d3d12_for_encode`, alimentar o resultado ao
   `AmfEncoder::Encode` por N frames (mesma cadência do modo existente) +
   `Flush`; exigir rc==CATRA_OK e bytes totais > 0 nas duas variantes.
5. Destruir tudo; `PASS` / return 0. Qualquer falha → return ≠ 0 com mensagem.
6. Rodar os modos existentes do tool na sequência (regressão do pool/cópia).

### Parte B — integração offline FSR 4 (C#)

**6. `ProcessingPipeline.cs` — fallback FSR4→FSR1.** No bloco `needUpscale`
(~linhas 227-233):

```csharp
if (needUpscale)
{
    int upscaleMethod = MapUpscaleMethod(config.UpscaleMethod);
    try
    {
        upscaleContext = _bridge.CreateUpscaler(
            meta.Width, meta.Height, config.TargetWidth, config.TargetHeight,
            upscaleMethod);
        haveUpscale = true;
    }
    catch (NativeBridgeException ex) when (upscaleMethod == UpscaleMethodFsr4)
    {
        // 2ª linha do fallback (a 1ª é o downgrade nativo em catra_upscale_create):
        // probe disse disponível mas o create FSR 4 falhou (ex.: DLL FFX sumiu
        // entre o probe e o create). Export segue com FSR 1.
        Trace.WriteLine(
            $"[ProcessingPipeline] FSR 4 upscale create failed ({ex.Message}); " +
            "falling back to FSR 1.");
        upscaleContext = _bridge.CreateUpscaler(
            meta.Width, meta.Height, config.TargetWidth, config.TargetHeight,
            UpscaleMethodFsr1);
        haveUpscale = true;
    }
}
```

- Se o retry FSR 1 também lançar → a exceção propaga e vira `ProcessResult`
  falho (catch geral existente). **NÃO** pular o upscale como a interpolação
  pula: o encoder é criado com `TargetWidth/TargetHeight`; sem upscale os
  frames chegariam em resolução de fonte num encoder dimensionado para o alvo.
  Comentar essa diferença no código.
- `MapUpscaleMethod`, `EncodeSingle`, `RunFrameLoop`: sem mudança.

**7. Default FSR 1 para export (plano Risco 3).**
- `ProcessingQueueService.cs` (~linha 560): `?? "fsr4"` → `?? "fsr1"`.
- `AppSettingsModel.cs` (~linha 57): `UpscaleMethod { get; set; } = "fsr1";`
  (com comentário: FSR 1 permanece default — limitação zero-MV do FSR 4
  documentada; usuário pode escolher fsr4).
- `tests/CATRA.Data.Tests/RepositoryTests.cs` (~linha 123): assert de seed
  `"fsr4"` → `"fsr1"`.
- Interação com story 05 (settings/UI): escolha explícita do usuário continua
  soberana (a key gravada vence os defaults); o toggle de playback da story 05
  lê a mesma key e o default A6 do plano também é FSR1 — consistente.

**8. Testes (`ProcessingPipelineTests.cs`).** No `FakeNativeBridge`
(que já registra `UpscaleCreateCount` e tem o padrão `ThrowOnInterpCreate`):
adicionar `NativeBridgeException? ThrowOnUpscaleCreateWhenMethod2` (lança em
`CreateUpscaler` somente quando `method == 2`) e registro
`List<int> UpscaleMethodsRequested` / `int LastUpscaleMethod`. Testes novos:

1. `UpscaleCreate_Fsr4Throws_FallsBackToFsr1_RunSucceeds`: config com upscale
   "fsr4" + fonte que exige upscale; `ThrowOnUpscaleCreateWhenMethod2` setado;
   esperar `result.Success == true`, `UpscaleCreateCount == 2`, último method
   == 1, encode/fluxo normais (espelhar
   `GracefulDegradation_InterpCreateFails_PipelineContinuesWithoutInterp`,
   linha ~515).
2. `UpscaleCreate_Fsr1AndFsr4Throw_RunFails`: fake lança em qualquer method de
   upscale; esperar `result.Success == false` com mensagem de erro (não lançar
   para fora de `ProcessAsync`).

**9. Validação E2E manual (hardware RX 9070 XT, com `-DCATRA_AMF_ROOT`).**
1. Build nativo (`scripts/build-native.ps1 -Configuration Release`) +
   `catra-interop-test --encode-d3d12-workaround` → PASS nas duas variantes;
   modos existentes do tool sem regressão.
2. Export perfil **DLNA** (upscale FSR 1, config atual) → `.mp4` válido:
   ffprobe (duração ≈ fonte, codec hevc, sem erros de decode) + reprodução.
   Critério A2 — desbloqueio do perfil.
3. Export com `upscale_method=fsr4` + DLLs FFX presentes (subtasks 01/02
   entregues) → `.mp4` válido (mesmos checks). Confirmar no log as linhas do
   contorno e a AUSÊNCIA de "CreateSurfaceFromDX12Native failed".
4. **Fallback sem DLLs:** renomear/mover as DLLs FFX do diretório de runtime →
   export com config fsr4 segue gerando `.mp4` válido via downgrade nativo
   (log "FSR 4 unavailable -> downgrade to FSR 1"), sem crash.
5. `dotnet build CATRA.sln` + `dotnet test` verdes (build-gate).

### Plano de contingência (detalhado)

Se o passo 9.1 ou 9.2 falhar em hardware real (pool texture rejeitada pelo AMF,
hang/device-removal na cópia, ou corrupção):
1. Registrar no reporte da subtask: contorno ineficaz no encoder (com log).
2. FSR 4 fica **restrito ao playback** nesta sprint (critério de export FSR 4
   cai; o de FSR 1 DLNA só é mantido se o contorno funcionar para ele — caso
   contrário também é cortado e o bug segue aberto).
3. Reverter o ramo D3D12 de `catra_encode_frame` para zero-copy (1 linha de
   decisão) ou gatear o helper por flag; manter a parte B (integração/fallback
   C#), que é independente.
4. Nota de status em `docs/FSR_AMF_ISSUE_CONTEXT.md` (tentativa N: cópia
   D3D12→D3D11 no encode — resultado).

## Critérios de Aceite

- [ ] Pipeline offline com upscale **FSR 4** entrega `.mp4` válido (ffprobe:
      abre, duração esperada, codec hevc, sem erros de decode) — a cópia
      D3D12→D3D11 na fronteira do encode contorna o bug AMF documentado.
- [ ] **A2:** export com upscale **FSR 1** também entrega `.mp4` válido com o
      mesmo perfil de export atual (DLNA desbloqueado).
- [ ] Contorno comprovadamente genérico: o ramo D3D12 de `catra_encode_frame`
      chama o helper para QUALQUER input D3D12; grep confirma ausência de
      branch/flag específico de FSR 4 no caminho de encode nativo.
- [ ] Smoke `catra-interop-test --encode-d3d12-workaround` PASSA nas variantes
      shareable e não-shareable (staging); skip gracioso sem `CATRA_HAS_AMF`.
- [ ] Sem DLLs FFX: export com config fsr4 segue funcionando com FSR 1
      (downgrade nativo; retry C# como 2ª linha) — sem crash, log claro.
- [ ] Testes novos passam: fallback FSR4→FSR1 (run succeeds, 2 creates) e
      falha dupla (run fails com `ProcessResult`); default de export fsr1
      (asserts de seed atualizados).
- [ ] Contratos preservados: `ShareCleanup` (sem leak de NT handle/ref),
      keyed-mutex consumer + pool-generation resync em `AmfEncoder::Encode`,
      `EncodeSingle` liberando a textura de upscale após `EncodeFrame`.
- [ ] Build nativo + `dotnet build CATRA.sln` + `dotnet test` verdes
      (build-gate); commit contém APENAS o fileScope (nunca `git add .`).
- [ ] Se o contorno falhar em hardware: plano de contingência aplicado e
      registrado (FSR 4 restrito ao playback) — reporte explícito.

## Dependências

- **subtask_01 (Story 01 — ffx_runtime/deploy):** necessária apenas para a
  parte B (presença das DLLs FFX no runtime para o export FSR 4 real). A parte
  A (contorno genérico / FSR 1 DLNA) independe dela.
- **subtask_02 (Story 02 — backend FSR 4):** necessária para validar o export
  com method=2 real. **Contratos exigidos do backend FSR 4:**
  (a) `Process` retorna com a textura de output **GPU-complete** (fence wait
  antes de retornar) e em **D3D12_RESOURCE_STATE_COMMON** (o contorno faz
  barrier COMMON→COPY_SOURCE assumindo esse estado — FSR 1 já cumpre);
  (b) **formato do output deve ser `B8G8R8A8_UNORM` (BGRA)**: o rascunho da
  subtask_02 especifica `R8G8B8A8_UNORM` (como o stub atual,
  `upscale_fsr4.cpp:383`), mas o encoder AMF é iniciado com `AMF_SURFACE_BGRA`
  e o issue doc registra rejeição de RGBA — se a implementação da subtask_02
  mantiver RGBA, alinhar o ajuste (desc do output) antes da validação E2E
  desta subtask. O contorno é agnóstico de formato; quem não é é o encoder.
- **Infra ST-15 (`d3d_interop`)** — já implementada; estendida (não
  substituída) pelo helper novo.
- **AMF SDK headers** (`-DCATRA_AMF_ROOT`, `lib/amf/` gitignored) na máquina de
  build para `CATRA_HAS_AMF` + smoke; runtime `amfrt64.dll` vem do driver AMD.
- **Ordem de execução:** a parte A pode ser implementada/validada ANTES das
  subtasks 01/02 (desbloqueia FSR 1 DLNA imediatamente); a parte B exige 01+02
  para o caminho feliz FSR 4 (o fallback já pode ser testado sem elas, pois o
  downgrade nativo devolve FSR 1).
- Consumidores: story 06 (QA/validação final) herda os roteiros E2E do passo 9.

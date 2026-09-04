# Subtask 03 — Spike + implementação/deliberação: FSR3 Frame Generation offline

| Campo            | Valor                                                                    |
|------------------|--------------------------------------------------------------------------|
| **Story**        | Story 03 — FSR3 Frame Generation no processamento offline (spike + implementação ou deliberação) |
| **Tipo**         | dev                                                                      |
| **Complexidade** | critical (R&D de API FFX: comportamento runtime não-documentado, sync GPU, zero-defect) |
| **Agente**       | developer-critical                                                       |
| **Dependências** | nenhuma (recon `.pi-subagents/artifacts/outputs/6167d934/context.md` já concluído) |

## Descrição

O fluxo `fsr3fg` hoje vira RIFE **silenciosamente** no processamento offline:
`MapInterpMethod("fsr3fg")` → method 2 (`ProcessingPipeline.cs:840-845`) e o
native trata qualquer método ≠ RIFE/NONE como "unavailable, using RIFE"
(`interp_rife.cpp:1028-1031`, warn log apenas). O contexto FG (`catra_fg`)
existe só para playback e **nenhum código C# chama `catra_fg_*`**.

Esta subtask abre com um **spike timeboxed** de viabilidade de rodar FFX
Frame Generation fora do swapchain de playback (contexto FG + dispatch
contra textura offscreen + readback para o pipeline offline) e termina em um
de dois caminhos:

- **Caminho A (GO):** implementação real de FG no pipeline offline.
- **Caminho B (NO-GO):** ADR documentando a impossibilidade + alternativa,
  **com aprovação explícita do usuário** (bloqueio de aceite, CA-3.3).

Em **ambos** os caminhos, o fallback silencioso `fsr3fg→RIFE` acaba
(CA-3.4).

### Evidência de viabilidade preliminar (investigação read-only, pré-spike)

Veredito preliminar do detalhador: **GO provável** — existe caminho de
dispatch sem swapchain na superfície da API e no sample oficial, mas a
confirmação depende das incógnitas listadas no Passo 1.

1. **A API expõe dispatch com outputs próprios (não atrelado a swapchain).**
   `ffxDispatchDescFrameGeneration` (`lib/FidelityFX-SDK-2.3.0/framegeneration/include/ffx_framegeneration.h`,
   `FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION`) aceita: `commandList`
   próprio, `presentColor` (textura fonte), `outputs[4]` (texturas destino
   fornecidas pela aplicação), `numGeneratedFrames`, `generationRect`,
   `backbufferTransferFunction`, `frameID`. Nada disso exige swapchain.
2. **Flag oficial para modo sem swapchain existe:**
   `FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY` (1<<3) — "context
   should only run frame interpolation and not modify the swapchain".
3. **O sample de referência exercita exatamente esse modo.**
   `lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp:1606-1658`:
   quando `!m_SwapChainContext`, o sample configura
   `NO_SWAPCHAIN_CONTEXT_NOTIFY`, usa o próprio command list, e faz
   `dispatchFg.outputs[0] = m_pInterpolationOutput` (render texture própria)
   com `presentColor` = backbuffer em `PIXEL_COMPUTE_READ`, chamando
   `ffx::Dispatch(m_FrameGenContext, dispatchFg)` diretamente.
4. **CATRA já tem 80% da infraestrutura.** `catra_fg.cpp` cria o contexto FG
   **separado** do contexto de swapchain FG (linha 403-427:
   `ffxCreateContextDescFrameGeneration` + `ffxCreateBackendDX12Desc` +
   version descriptor, device = device D3D12 do interop). O trampoline
   `FgDispatchTrampoline` (linha 179-192) já chama
   `FfxRuntime::Functions().Dispatch()` — o plumbing de GetProcAddress para
   CreateContext/DestroyContext/Configure/Query/Dispatch já existe
   (`ffx_runtime.h`). Playback roda **sem Prepare dispatch** (sem depth/MV):
   comentário em `catra_fg.cpp:15-16` documenta que o runtime cai para
   optical flow interno — assunção que o spike deve revalidar no modo
   offline.
5. **Superfície swapchain é isolada e dispensável offline.** Todos os
   descritores de swapchain vivem em `dx12/ffx_api_framegeneration_dx12.h`
   (`FFX_API_EFFECT_ID_FRAMEGENERATIONSWAPCHAIN*`) e nenhum é necessário se
   não houver Present.

### Incógnitas que o spike DEVE resolver (motivo de não ser GO direto)

- **U1:** `ffxConfigureDescFrameGeneration` aceita `swapChain = nullptr`?
  O sample sempre passa um swapchain DXGI real (mesmo no ramo sem FG
  swapchain context). Se recusar, alternativa: HWND oculto + swapchain
  dummy nunca apresentado (viável porém frágil).
- **U2:** FG dispatch sem Prepare (optical flow interno) produz frames
  válidos **no contexto offline** (sem pacing/present)? Validar com frames
  não-pretos e contagem de quadros do output.
- **U3:** Barreiras D3D12 e ownership de `outputs[]` (pitfalls #1/#5 do
  AGENTS.md: estado UNORDERED_ACCESS/COMMON sem barrier = saída preta).
- **U4:** Formato: saída FG é BGRA; o encoder AMF consome NV12 — existe
  conversor NV12→BGRA (`nv12_to_bgra_shader.cpp`) mas não o inverso; decidir
  rota (novo compute shader BGRA→NV12, ou codificar BGRA).
- **U5:** Contrato `frameID` (+1 exato por dispatch; qualquer gap reseta o
  FG) aplicado a um job offline com pares de frames.
- **U6:** Performance: optical flow interno + geração 2× em resolução de
  vídeo — medir custo/tempo de job (sem timebox de performance nesta
  subtask, mas medir).

## Arquivos Alvo (fileScope)

### Spike (protótipo descartável ou tool standalone)
- `native/catra-gpu/tools/` — novo smoke test `fg_offline_smoke_test`
  (padrão de `fg_smoke_test` existente): cria contexto FG sem swapchain
  (U1), dispatch de N pares de frames sintéticos/textura própria para
  `outputs[0]` offscreen, readback staging + validação (não-preto, dims).
- `native/catra-gpu/catra_fg.cpp` — referência (read-only no spike):
  `FgRenderer::Create` (linhas ~340-500) como modelo de Create/Configure.
- `native/catra-gpu/ffx_runtime.cpp` / `ffx_runtime.h` — read-only: funções
  já carregadas; nenhuma mudança esperada.

### Caminho A (GO) — implementação
- `native/catra-gpu/catra_fg_offline.cpp` (novo) ou extensão de `catra_fg.cpp`:
  contexto FG offline (sem swapchain), entrada textura D3D12 compartilhada
  (interop share D3D11→D3D12, padrão keyed-mutex existente), dispatch contra
  `outputs[]` próprios, fence wait bounded (padrão `kFenceWaitTimeoutMs`),
  teardown por geração. **Sem exceção escapando** (GuardCabi).
- `native/catra-gpu/catra_gpu.h` / `catra_gpu.cpp` — novos entrypoints C ABI
  (ex.: `catra_fg_offline_create/process/free`) com retornos POD e handles
  densos; echo de handle; `catra_free` para frame arrays.
- `native/catra-gpu/CMakeLists.txt` — registro do novo TU + smoke test.
- `src/CATRA.Services/Native/NativeBridge.cs` — P/Invoke dos novos símbolos.
- `src/CATRA.Core/Interfaces/INativeBridge.cs` — contrato.
- `src/CATRA.Services/Processing/ProcessingPipeline.cs` —
  `CreateInterpolation`/`MapInterpMethod` (linhas ~215-234, 840-845):
  rota `fsr3fg` real + sinalização explícita de fallback (ver "Passo 4").
- Conversão BGRA→NV12 (U4): novo compute shader ou rota documentada.

### Caminho B (NO-GO) — documentação
- `.agents/decisions/ADR-XXX-fsr3-fg-offline.md` (TEMPLATE.md),
  status **Proposed** até aprovação do usuário.
- `docs/fsr3-fg-offline-spike-report.md` — relatório do spike (CA-3.1).

### Qualquer caminho (obrigatório — CA-3.4)
- `src/CATRA.Services/Processing/ProcessingPipeline.cs` — evento/log
  explícito quando `fsr3fg` cai para RIFE (consumível pela Story 04).
- `native/catra-gpu/interp_rife.cpp:1028-1031` — elevar o warn para nível
  e formato consumíveis (não silenciar).
- `tests/CATRA.Services.Tests/Processing/` — teste de que o evento dispara.

## Passos

### Passo 1 — Spike timeboxed de viabilidade (timebox: 8h de trabalho)

**Entrada fixa:** POC standalone em `native/catra-gpu/tools/fg_offline_smoke_test`
(compilado pelo CMake existente, padrão POST_BUILD das DLLs FFX).

1. **U1 — Configure sem swapchain (gate principal, ~2h):** criar contexto FG
   (`ffxCreateContextDescFrameGeneration` + backend DX12 desc + version
   desc, como `catra_fg.cpp:403-427`) e `Configure` com
   `swapChain = nullptr`, `frameGenerationEnabled = true`, flag
   `FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY`. Registrar o
   `ffxReturnCode_t`. Se recusar, tentar: (a) swapchain dummy em HWND
   oculto nunca apresentado; (b) documentar como bloqueio.
2. **U2 — Dispatch offscreen (~3h):** 2 texturas fonte (frames sintéticos
   distinguíveis, ex.: gradiente deslocado) como `presentColor`;
   `outputs[0]` = textura própria UAV-capable em estado correto;
   `commandList` próprio; `frameID` +1 por dispatch; fence wait bounded.
   Readback staging → validar: dims corretas, não-preta, diferente das duas
   fontes (i.e., é um frame *gerado*). Repetir ≥30 pares.
3. **U3 — Barreiras/estados (~1h):** se saída preta, testar transições
   (COMMON ↔ UNORDERED_ACCESS ↔ PIXEL_COMPUTE_READ) conforme o sample
   (`FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ` em `fsrapirendermodule.cpp:1641-1642`).
4. **U6 — Custo (~1h):** medir ms/frame da geração em resolução 1080p na
   máquina dev. Registrar no relatório.
5. **Decisão GO/NO-GO explícita** (não-adivinhar):
   - **GO** ⇔ U1 ok (direto ou via dummy documentado) **E** U2 produz frames
     válidos consistentemente **E** U3 resolvido.
   - **NO-GO** ⇔ qualquer gate U1/U2 falha sem workaround aceitável dentro
     do timebox (não estender o timebox sem nova decisão do orquestrador).
6. **Saída obrigatória:** `docs/fsr3-fg-offline-spike-report.md` com:
   conclusão GO/NO-GO, evidência por incógnita (logs, retcodes, frames de
   validação), restrições encontradas, medição U6 (CA-3.1).

### Passo 2A — Caminho A (GO): implementação

1. **Native:** `catra_fg_offline` (TU novo ou extensão): Create (gate
   `FfxRuntime::IsAvailable()` → `CATRA_ERR_NOT_IMPL`, padrão
   `catra_fg.cpp`), Process(pares de frames via interop share) → gera
   `floor(targetFps/srcFps)-1` frames intermediários por par (alinhado com
   a semântica `framesPerPair` de `interp_rife.cpp`), saída em texturas
   caller-owned (release via `catra_release_texture`), fence bounded,
   teardown por geração, GuardCabi em tudo.
2. **ABI:** entrypoints em `catra_gpu.h`; handles densos com echo; sem
   alocação vazando (frame arrays via `catra_free`).
3. **Formato (U4):** decidir rota BGRA→NV12 documentando a escolha.
4. **Managed:** `NativeBridge` + `INativeBridge` + `ProcessingPipeline`
   rotando `fsr3fg` para o novo backend com availability probe
   (`catra_is_fg_available`-like sem HWND).
5. **Defensive programming obrigatório:** timeout em toda espera GPU;
   validação de args (null dims 0, fps ≤ 0 → `CATRA_ERR_INVALID_ARG`);
   reset de `frameID` apenas por contrato (+1 exato); teardown idempotente.
6. **Validação CA-3.2:** processar arquivo de teste com `fsr3fg` → contagem
   de quadros do output > contagem fonte e coerente com fps alvo (log +
   ffprobe do output). Testar também em máquina/condição sem FFX (fallback
   sinalizado, não silencioso).

### Passo 2B — Caminho B (NO-GO): ADR + notificação de fallback

1. Escrever `.agents/decisions/ADR-XXX-fsr3-fg-offline.md` pelo
   `TEMPLATE.md`: Context (evidência do spike por incógnita), Questões
   (alternativas: dummy swapchain descartado e por quê, manter RIFE,
   remover opção `fsr3fg` da UI, optical flow próprio), Decisão,
   Consequências + Mitigação, Referências ao relatório do spike.
2. **Submeter o ADR ao usuário para aprovação explícita** (via
   `contact_supervisor` com reason `need_decision`, anexando o ADR
   completo). **Sem aprovação do usuário esta subtask não pode ser marcada
   completed** (CA-3.3 — bloqueio de aceite).
3. Após aprovação: implementar o fallback sinalizado (Passo 4) e, se a
   decisão for remover/marcar `fsr3fg` como indisponível, ajustar
   `SettingsViewModel.cs:104` e `MapInterpMethod` conforme decidido.

### Passo 3 — Qualquer caminho: fim do fallback silencioso (CA-3.4)

1. `ProcessingPipeline.CreateInterpolation` (`~215-234`): quando método
   pedido = `fsr3fg` e o backend FG offline não for usado (indisponível ou
   NO-GO), **emitir evento/log estruturado explícito** ("interp fallback:
   fsr3fg→rife, motivo=...") antes de prosseguir com RIFE — consumível
   pela Story 04.
2. `interp_rife.cpp:1028-1031`: manter o warn mas com formato estável
   (não depender dele como único sinal; o evento managed é a fonte).
3. Teste unitário: mock/cenário `fsr3fg` sem FG → evento disparado 1×;
   cenário `rife` → nenhum evento de fallback.

### Passo 4 — Build gate + regressão (CA-3.6, CA-3.7)

1. Build nativo (`.\scripts\build-native.ps1`) + build solution.
2. Testes existentes: `CATRA.Core.Tests`, `CATRA.Data.Tests`,
   `CATRA.Services.Tests`, `CATRA.UI.Tests` — todos verdes.
3. Regressão manual: job `rife` intacto (mesma contagem de quadros de
   baseline); código native de `catra_fg` de playback **não removido nem
   alterado em comportamento** (CA-3.6).
4. CA-3.5: confirmar que nenhum componente de driver é exigido — apenas as
   8 DLLs FFX do bundle em `runtimes/win-x64/native/`.

## Critérios de Aceite

- [ ] **CA-S3.1** — Relatório do spike em `docs/fsr3-fg-offline-spike-report.md`,
      dentro do timebox (8h), com GO/NO-GO explícito, evidência por
      incógnita (U1-U3, U6) e restrições (espelha CA-3.1).
- [ ] **CA-S3.2** *(Caminho A)* — Fluxo `fsr3fg` aplica FG real no
      processamento: output com quadros gerados além dos fonte, verificável
      por contagem de quadros e/ou log (espelha CA-3.2).
- [ ] **CA-S3.3** *(Caminho B)* — ADR escrito e **aprovado explicitamente
      pelo usuário**; sem aprovação, subtask fica bloqueada (espelha CA-3.3).
- [ ] **CA-S3.4** — Fallback `fsr3fg→RIFE` deixa de ser silencioso em
      **qualquer** caminho: evento/log estruturado disparado e testado
      (espelha CA-3.4; alimenta Story 04).
- [ ] **CA-S3.5** — Sem dependência de componentes instalados por driver;
      só DLLs redistribuídas do bundle FFX (espelha CA-3.5).
- [ ] **CA-S3.6** — Sem regressão: fluxo `rife` intacto; `catra_fg` de
      playback preservado (espelha CA-3.6).
- [ ] **CA-S3.7** — Testes existentes passam; build gate verde (espelha CA-3.7).

## Dependências

- Nenhuma subtask anterior bloqueia esta.
- Story 04 (eventos de fallback na UI) **consome** o evento do Passo 3 —
  o formato do evento deve ser acordado ao implementar (não bloqueia aqui).
- ADR (Caminho B) depende de aprovação do usuário — fluxo via
  orquestrador/supervisor, não decidir unilateralmente.

## Notas

- Veredito preliminar do detalhamento: **GO provável** — o sample oficial
  (`fsrapirendermodule.cpp:1606-1658`) prova o modo dispatch-direto sem FG
  swapchain, e CATRA já possui contexto FG separado + plumbing FFX completo.
  As incógnitas U1 (`swapChain=nullptr`) e U2 (optical flow interno offline)
  são as únicas que podem derrubar para NO-GO.
- Risco residual mesmo com GO: custo de optical flow interno por frame pode
  tornar o job offline muito lento (U6) — decisão de produto, não técnica.
- Não reutilizar o contexto de playback `catra_fg` para o offline: o
  contrato de `frameID` e o lifecycle de teardown por geração são distintos.

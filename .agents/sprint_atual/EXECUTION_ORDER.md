# Ordem de Execução — Sprint 04

Validação PO das 6 subtasks do SPRINT_04 (integração FidelityFX SDK — plano
`.agents/specs/fsr-sdk-integration_plan.md`, ajustes A1–A7) + ordenação.

| Ordem | Subtask | Agente | Dependência |
|-------|---------|--------|-------------|
| 1 | subtask_01 — Infra FFX (ffx_runtime, smoke das 8 DLLs, deploy) | developer-alta | Nenhuma (fundação do sprint) |
| 2 | subtask_02 — Backend FSR 4/3.1 nativo (zero-MV) | developer-alta | subtask_01 (BLOQUEANTE) |
| 3 | subtask_03 — Contorno AMF genérico no encode + FSR 4 offline | developer-alta | subtask_01 (parte B) + subtask_02 (validação E2E FSR 4; contrato BGRA) |
| 4 | subtask_04 — Módulo nativo catra_fg_* (FG playback) | developer-alta | subtask_01 (BLOQUEANTE); subtask_02 NÃO é dependência — posição 4 é só serialização de merge em catra_gpu.* |
| 5 | subtask_05 — Integração C#/WPF (renderer FG, factory, settings) | developer-alta | subtask_04 (BLOQUEANTE) + subtask_02 (matriz A6) + subtask_03 (serialização de merge em catra_gpu.*, exports D2) |
| 6 | subtask_06 — QA final + docs FSR_FFX_INTEGRATION.md | qa-tester-alta | subtasks 01–05 (todas BLOQUEANTES) |

**Justificativa da ordenação:** subtask_01 é pré-requisito de todo o código nativo
FFX (loader + DLLs + include dirs). 02 vem antes de 03 porque o caminho feliz de
03 (export FSR 4) e o contrato de formato do output dependem do backend de 02.
04 é tecnicamente independente de 02/03, mas toca `catra_gpu.h/.cpp` (entry
points + registry) — arquivos também modificados por 02, 03 e 05 — então é
serializada para evitar conflito de merge. 05 consome a ABI de 04 e o backend de
02, e seus exports aditivos D2 tocam os mesmos arquivos nativos de 03 (merge só
com 03 integrada, como a própria subtask declara). 06 valida tudo (A1–A7).

## Decisões do PO

### D-PO-1 — subtask_02: novo entry point `catra_upscale_reset` → **APROVADO**
Desvio do fileScope da story 02 (que limitava `catra_gpu.h` a docstring), porém
**necessário e mínimo**: o critério de aceite "reset=true em scene-cut" é
impossível sem um canal do caller — verificado no código que a ABI atual
(`catra_gpu.h`) não possui nenhum mecanismo de reset/scene-cut. Aprovo com as
condições:
- Entry point estritamente aditivo (`catra_upscale_reset(int ctx)`), sob
  `GuardCabi`; no-op para FSR1/passthrough; `CATRA_ERR_CONTEXT` para handle
  desconhecido; docstring completa no padrão do header.
- Alternativa rejeitada (reset todo frame) corretamente descartada — desabilitaria
  a acumulação temporal do FSR 4.
- **Registro:** nenhum consumidor C# existe nesta sprint (subtask_05 não o
  consome). O reset de primeiro frame (`pendingReset=true` no create) cobre os
  casos reais atuais (novo contexto por episódio no export; novo contexto por
  sessão/geometria no playback). A sinalização de scene-cut mid-stream fica
  **deferida** e deve constar como limitação em `docs/FSR_FFX_INTEGRATION.md`
  (subtask_06).

### D-PO-2 — subtask_05 (D2): exports nativos aditivos `catra_d3d12_to_d3d11` e `catra_fg_stats` → **APROVADOS**
A matriz A6 do plano aprovado ("uso simultâneo PERMITIDO — upscale → FG") é
**impraticável com a ABI atual** — fatos verificados no código:
- `catra_upscale_process` retorna `ID3D12Resource*` criado com
  `D3D12_HEAP_FLAG_NONE` (`upscale_fsr4.cpp:391`; idem FSR1) → não compartilhável;
- `interop_share_d3d12_to_d3d11` é helper interno, **não exportado** na C ABI.
Vetar D2 exigiria redefinir A6 (upscale off obrigatório com FG), o que violaria o
plano já aprovado — desvio maior que os 2 exports. Aprovo com as condições:
- Estritamente aditivos: nenhuma assinatura existente muda.
- `catra_d3d12_to_d3d11`: deve aceitar o output de **ambos** os backends
  (FSR1 e FSR4, ambos D3D12 não-shareable); bloquear até a cópia GPU completar
  (fence wait) para o caller liberar o D3D12 imediatamente após o retorno;
  `*out = null` em todo caminho de erro.
- `catra_fg_stats`: reutiliza os contadores do trampoline de present já
  especificado na subtask_04 (total/gerados).
- Verificação por `dumpbin /exports` no build-gate da subtask_05.
- Merge apenas com a subtask_03 já integrada (serialização declarada — ambos
  tocam `catra_gpu.h/.cpp`).

### D-PO-3 — subtask_03: default de export passa a FSR 1 → **CONFIRMADO**
O plano aprovado é explícito: "FSR 1 (EASU) permanece default para export"
(Escopo + Risco 3), reiterado nas stories 02/03. Verificado que hoje o default é
`fsr4` em 3 pontos. **Ajuste obrigatório:** a subtask_03 listou
`ProcessingQueueService.cs` (`?? "fsr4"` → `?? "fsr1"`), `AppSettingsModel.cs`
(default) e o assert de teste, mas **OMITIU `DatabaseInitializer.cs:85`**, que
faz seed `("upscale_method", "fsr4")` via INSERT OR IGNORE — sem alterar o seed,
o fallback `?? "fsr1"` nunca é atingido em instalações novas e a mudança de
default é ineficaz. Fica **obrigatório** incluir no fileScope da subtask_03:
- `src/CATRA.Data/Database/DatabaseInitializer.cs` — seed `"fsr4"` → `"fsr1"`.
- Instalações existentes já semeadas com `fsr4` mantêm o valor (INSERT OR IGNORE
  preserva) — aceito; documentar no reporte da subtask.

### D-PO-4 — Ajuste obrigatório na subtask_02: formato do output BGRA
O passo 6.4 da subtask_02 especifica output `R8G8B8A8_UNORM` (herdado do stub,
`upscale_fsr4.cpp:383`). Fatos verificados: FSR1 produz `B8G8R8A8_UNORM`
(`upscale_fsr1.cpp:345`), o encoder AMF é inicializado com `AMF_SURFACE_BGRA` e o
`docs/FSR_AMF_ISSUE_CONTEXT.md` registra rejeição de RGBA. **Determino:** o
output do backend FSR 4 deve ser `DXGI_FORMAT_B8G8R8A8_UNORM` (BGRA), alinhando
com FSR1 e com o contrato declarado pela subtask_03 (pré-requisito do contorno
AMF e do export). O smoke `--fsr4-smoke` deve validar o formato.

### Validação por subtask (atende story + plano A1–A7 / complexidade / dependências)

| Subtask | Atende story+plano? | Complexidade/agente | Dependências corretas? |
|---------|---------------------|---------------------|------------------------|
| 01 | Sim (A1 lista completa das 8 DLLs + A7 LICENSE; desvio do `docs/fsr/README.md` ponteiro justificado pela nota da orquestração — aprovado) | Alta/developer-alta adequada (C++/Win32, wiring 3 camadas, edge cases) | Sim — nenhuma; bloqueia 02/03/04 corretamente |
| 02 | Sim, com D-PO-1 (aprova reset) e D-PO-4 (BGRA obrigatório) | Alta/developer-alta adequada | Sim — 01 BLOQUEANTE; 03/05 como consumidores futuros correto |
| 03 | Sim (A2 genérico + contingência Risco 1), com D-PO-3 (ajuste DatabaseInitializer). Desvio de local (d3d_interop/catra_gpu.cpp em vez de encode_amf.cpp) justificado por fatos verificados — aprovado | Alta/developer-alta adequada (cross-API sync, E2E encode) | Sim — 01/02 para a parte B; parte A independente, declarado corretamente |
| 04 | Sim (ABI exatamente A3; base do fallback A4; observer para medição A4/A5) | Alta/developer-alta adequada (swapchain FG, resize/recreate, smoke HWND real) | Sim — 01 BLOQUEANTE; 02 corretamente NÃO-dependência |
| 05 | Sim (A4 fallback, A5 medição de drift, A6 matriz), com D-PO-2 (exports aditivos aprovados) | Alta/developer-alta adequada (multi-camada C#↔P/Invoke↔GPU, factory+fallback, settings/UI) | Sim — 04 BLOQUEANTE, 02 consumo A6, 03 serialização de merge, 01 indireta |
| 06 | Sim (matriz cobre A1–A7, falhas induzidas, docs com 6 seções) | Alta/qa-tester-alta adequada (E2E multi-layer + medições) | Sim — todas as 5 BLOQUEANTES |

### Notas de coordenação (obrigatórias para os implementadores)
1. **Contrato ffx_runtime:** a subtask_02 assumiu nomes (`FfxRuntimeIsLoaded`/
   `FfxFunctionsGet`) diferentes da API real especificada na subtask_01
   (`catra::ffx::FfxRuntime::Load/IsLoaded/Functions/...`). Como 01 executa
   primeiro, os implementadores de 02 e 04 devem adaptar as chamadas à API real
   (ambas as subtasks já preveem isso) — NÃO reimplementar o loader.
2. **Commits:** apenas arquivos do fileScope de cada subtask — nunca `git add .`
   (repo sujo fora do escopo, Risco 5 do plano).
3. **Conflitos previstos em `catra_gpu.h/.cpp`:** subtasks 02, 03, 04 e 05 tocam
   esses arquivos; a ordem 02→03→04→05 serializa os merges. Cada subtask deve
   fazer rebase antes de iniciar.

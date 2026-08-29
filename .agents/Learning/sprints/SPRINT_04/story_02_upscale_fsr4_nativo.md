# Story 02 — Backend de upscale FSR 4/3.1 nativo via FFX API (modo vídeo zero-MV)

**Sprint:** SPRINT_04 · **Spec/Plano:** `.agents/specs/fsr-sdk-integration_plan.md` (aprovado 2026-08-13)
**Tipo:** dev

## Descrição

Substituir o stub atual de FSR 4 (`native/catra-gpu/upscale_fsr4.cpp`, compile-gated por
`CATRA_HAS_FSR4` e nunca compilado) por uma implementação **real e runtime-loaded** via
FFX API 2.x (`amd_fidelityfx_upscaler_dx12.dll` — FSR 4 ML em RDNA 4 com fallback FSR 3.1),
usando a infra `ffx_runtime` da Story 01.

Implementação:
- Create: `ffxCreateContextDescUpscale`; dispatch por frame: `ffxDispatchDescUpscale`.
- **Modo vídeo (zero-MV)** — a API é temporal e exige `color`, `depth`, `motionVectors`,
  `jitterOffset`; para vídeo (sem MV de engine): buffer de motion vectors zerado
  (RG32F, renderSize), depth dummy, `jitter=(0,0)` e `reset=true` em scene-cut.
  Qualidade inferior a engine com MV — limitação documentada; **FSR 1 (EASU) permanece
  default para export**.
- Disponível como `CATRA_UPSCALE_FSR4 = 2` na ABI flat C existente (`catra_gpu.h`).
- Detecção em runtime: `catra_is_fsr4_available()` passa a significar "loader + DLLs FFX
  presentes + adapter compatível" — **docstring em `catra_gpu.h` será atualizada** para a
  nova semântica runtime-load (não depende mais de build com `CATRA_HAS_FSR4`).
- Fallback graceful: sem DLLs FFX em runtime, upscale cai para FSR 1 sem crash.

## FileScope (preliminar)

**Criar:** nada novo além de helpers internos, se necessários.

**Modificar:**
- `native/catra-gpu/upscale_fsr4.h` / `upscale_fsr4.cpp` — substituição do stub por implementação FFX runtime-loaded (modo vídeo zero-MV, reset em scene-cut)
- `native/catra-gpu/catra_gpu.h` — atualização da docstring de `catra_is_fsr4_available()`
- `native/catra-gpu/catra_gpu.cpp` — wiring de `CATRA_UPSCALE_FSR4=2` ao novo backend e ao probe runtime
- `native/catra-gpu/CMakeLists.txt` — somente se necessário linkar/incluir algo do `ffx_runtime` (já criado na Story 01)

**NÃO tocar:** `upscale_fsr1.cpp` (permanece referência/default de export), `encode_amf.cpp`
(Story 03), código C# (Story 05).

## Critérios de Aceite

- [ ] Build passa **sem** SDK FSR em build-time (tudo runtime-loaded via Story 01).
- [ ] Com DLLs presentes + GPU AMD: `catra_is_fsr4_available()` retorna **1** e upscale
      method=2 produz output correto — validado por smoke test nativo e/ou QA visual
      (frame de saída nítido, sem corruption, dimensões corretas).
- [ ] Sem DLLs FFX em runtime: `catra_is_fsr4_available()` retorna 0 e upscale cai
      automaticamente para FSR 1, sem crash.
- [ ] Reset (`reset=true`) aplicado em scene-cut; jitter=(0,0) e MV zerado confirmados no código.
- [ ] Testes/smoke existentes passam sem regressão.

## Dependências

- **Story 01** (headers vendorados, `ffx_runtime`, deploy das DLLs).

## Notas

- Risco conhecido: qualidade do upscale temporal zero-MV (ghosting em movimento/cortes).
  Mitigação: reset em scene-cut + FSR 1 como default de export + documentação (Story 06).
- Referência de uso da API: `docs/fsr/fsrapirendermodule.cpp` (vendorado na Story 01).

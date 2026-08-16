# Story 03 — Contorno AMF genérico na fronteira do encode + integração offline FSR 4

**Sprint:** SPRINT_04 · **Spec/Plano:** `.agents/specs/fsr-sdk-integration_plan.md` (aprovado 2026-08-13)
**Tipo:** dev

## Descrição

Duas entregas acopladas:

1. **Contorno genérico do bug AMF (`docs/FSR_AMF_ISSUE_CONTEXT.md`) na fronteira do
   encode** (**A2**): qualquer input D3D12 que chegue ao `catra_encode_frame` recebe
   **cópia GPU para D3D11** antes do `CreateSurfaceFromDX12Native` (reutilizando a infra
   de interop existente em `d3d_interop.cpp` — device D3D12 compartilhado, pool de
   texturas). O contorno é **genérico** (não específico de FSR 4): desbloqueia também o
   **export com upscale FSR 1 (perfil DLNA)**, que hoje está bloqueado pelo mesmo bug.
   A correção do bug AMF em si permanece fora de escopo (bug aberto separado).

2. **Integração offline do upscale FSR 4**: o pipeline de export passa a poder usar
   `method=2` (FSR 4 via Story 02) quando disponível, com fallback graceful para FSR 1
   se as DLLs FFX estiverem ausentes. **FSR 1 (EASU) permanece default para export**
   (limitação zero-MV documentada).

Se nem a cópia D3D12→D3D11 funcionar no encoder, o upscale FSR 4 fica restrito ao
playback nesta sprint (plano de contingência do Risco 1) — deve ser registrado no reporte.

## FileScope (preliminar)

**Modificar:**
- `native/catra-gpu/encode_amf.cpp` / `encode_amf.h` — cópia GPU D3D12→D3D11 na fronteira de `catra_encode_frame` antes de `CreateSurfaceFromDX12Native` (qualquer input D3D12)
- `native/catra-gpu/d3d_interop.cpp` / `d3d_interop.h` — somente se necessário expor/reusar helper de cópia D3D12→D3D11
- `src/CATRA.Services/Processing/*` — serviço/orquestração de export que seleciona o método de upscale (confirmar arquivo exato: pipeline que hoje usa FSR 1) para suportar method=2 com fallback

**NÃO tocar:** `upscale_fsr1.cpp`, `upscale_fsr4.cpp` (Story 02), pipeline RIFE de
interpolação offline, arquivos de Casting/Library.

## Critérios de Aceite

- [ ] Pipeline offline com upscale **FSR 4** entrega arquivo **.mp4 válido** (abre, dura o
      esperado, sem corruption) — a cópia D3D12→D3D11 na fronteira do encode contorna o
      bug AMF documentado.
- [ ] **A2:** Export com upscale **FSR 1 também entrega .mp4 válido** (contorno genérico
      desbloqueia o perfil DLNA) — validado com o mesmo perfil de export atual.
- [ ] O contorno é comprovadamente genérico: qualquer input D3D12 em `catra_encode_frame`
      passa pela cópia (não há branch específico de FSR 4).
- [ ] Sem DLLs FFX: export segue funcionando com FSR 1 (fallback graceful, sem crash).
- [ ] Testes existentes passam sem regressão.

## Dependências

- **Story 01** (infra runtime/deploy).
- **Story 02** (backend FSR 4) — para a parte de integração offline com method=2.
  A parte do contorno genérico (A2/FSR 1 DLNA) não depende da Story 02 e pode ser
  implementada/validada antes.

## Notas

- Validação de .mp4 válido: reprodução/ffprobe (duração, codec, sem erros de decode).
- Risco 1 do plano: se a cópia D3D12→D3D11 também falhar no encoder, escopo desta story
  é reduzido (FSR 4 só playback) com registro explícito.

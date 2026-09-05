---
name: ADR-001
title: FSR 3 Frame Generation inviável no processamento offline — manter RIFE com sinalização explícita
status: Proposed
date: 2026-09-05
deciders: usuário (aprovação pendente — CA-3.3), developer-critical (spike), product-owner
---

# ADR-001: FSR 3 Frame Generation inviável no processamento offline

## Context

A spec da Sprint 06 exige "corrija a geração de quadros fsr": o fluxo `fsr3fg`
do processamento (export) deve aplicar frame generation de verdade. Hoje a
opção cai **silenciosamente** para RIFE (`interp_rife.cpp:1028-1031`).

Spike de viabilidade executado em máquina real (RX 9070 XT, RDNA 4, FFX bundle
2.3.0) com POC standalone (`native/catra-gpu/tools/fg_offline_smoke_test.cpp`):

- **U1**: contexto FG sem swapchain só é aceito pelo provider 4.0.1 com flag
  `NO_SWAPCHAIN_CONTEXT_NOTIFY`; provider 3.1.6 **recusa** (`ret=6`).
- **U2 (gate)**: dispatch offscreen **não gera frames** — `interpHits=0`,
  `passthroughHits=10/10`, saída byte-idêntica à fonte em 7 variações de
  probe (com/sem Prepare, motion vectors, RGBA, numGen=2). Optical flow nunca
  dispara.
- **Controle decisivo**: no caminho playback (swapchain FG + Present), a mesma
  máquina gera frames (`generated=90, ratio=2.00`).

Conclusão: o provider FG do FFX 2.3.0 **atrela a geração ao ciclo de Present**
(pacing/vsync). Frame generation genuinamente offline (render target → readback
→ encoder) não é suportada pelo bundle atual.

Relatório completo: `docs/fsr/fg_offline_spike.md` (+ logs
`native/catra-gpu/build/Release/fg_offline_run_v2/v3.log`).

## Questões

- Como tratar a opção `fsr3fg` no processamento offline?
- Alternativas analisadas:
  - **(a)** Manter RIFE como backend de interpolação offline do `fsr3fg`, com
    fallback **explícito sinalizado** (evento tipado na UI de processamento +
    log — requisito CA6/CA-3.4, obrigatório em qualquer caminho).
  - **(b)** Swapchain dummy em HWND oculto + Present headless — **descartado**:
    geração atrelada a pacing de Present real; frágil (janela oculta, vsync,
    readback do backbuffer) e viola o modelo offline do pipeline.
  - **(c)** Remover/desabilitar a opção `fsr3fg` da UI de processamento até
    existir suporte real.
  - **(d)** Revisitar quando houver bundle FFX > 2.3.0 com provider FG
    offscreen genuíno, ou provider 3.1.6 que aceite contexto sem swapchain em
    RDNA 4 (monitorar release notes do FSR SDK).

## Decisão (proposta — aguardando aprovação do usuário)

**Combinar (a) + (c) + (d):**

1. **(a)** O fluxo `fsr3fg` no processamento offline passa a usar RIFE com
   **sinalização explícita**: evento de fallback tipado ("FSR 3 FrameGen não é
   suportado offline → usando RIFE") na tela de processamento (Story 04) e log
   estruturado. Fim do fallback silencioso.
2. **(c)** A opção na UI de Settings é **renomeada/annotada** para deixar claro
   que frame generation real ocorre apenas em playback: label ex.
   "Interpolação (RIFE)" como método, com `fsr3fg` exibido como
   "FSR3 FG — indisponível offline (usa RIFE)". *Variante mais agressiva
   (remover a opção entirely) requer escolha do usuário.*
3. **(d)** Reavaliar em cada upgrade do bundle FFX; o spike test
   (`catra-fg-offline-test`) fica no repo como ferramenta de revalidação.

## Consequências

### Positivas
- Usuário nunca mais é enganado: sabe exatamente qual algoritmo rodou (CA6).
- Pipeline offline permanece robusto (sem HWND/vsync no caminho de export).
- RIFE já entrega interpolação real hoje (2x/4x) em qualquer GPU via
  DirectML→CPU — vendor-neutro, atende CA3.

### Negativas
- "Geração de quadros FSR" não é entregue no processamento offline com o
  bundle FFX atual — limitação do SDK, não do CATRA.
- Opção `fsr3fg` fica semanticamente = RIFE até (d) se concretizar.

### Mitigação
- Sinalização explícita na UI (a) + label honesto (c).
- Ferramenta de revalidação pronta para futuros bundles (d).
- FG real permanece disponível no caminho de playback (`catra_fg`) para
  integração futura, fora do escopo desta sprint.

## Referências

- `docs/fsr/fg_offline_spike.md` — relatório do spike (evidências U1–U6)
- `.agents/specs/non-amd-fsr-processing_plan.md` — CA2/CA3/CA6
- `.agents/sprint_atual/subtask_03_fsr3_fg_offline.md`
- `lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp:1606-1658`
- `native/catra-gpu/catra_fg.cpp` (FG playback)

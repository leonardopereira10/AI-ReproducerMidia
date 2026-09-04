# Story 03 — FSR3 Frame Generation no processamento offline (spike + implementação ou deliberação)

**Tipo:** dev
**Dependências:** nenhuma
**Spec principal:** `.agents/specs/non-amd-fsr-processing_plan.md` (Escopo 1+2; CA2, CA3)

## Descrição

O fluxo `fsr3fg` hoje vira RIFE silenciosamente: `MapInterpMethod("fsr3fg")`
mapeia para method 2 e o native trata qualquer método ≠ RIFE como
indisponível (`interp_rife.cpp:1028-1031`). O contexto FG (`catra_fg`) existe
apenas para playback e não é chamado pelo C#. Esta story inicia com um
**spike timeboxed** de viabilidade de aplicar FFX Frame Generation fora do
swapchain de playback (contexto FG + dispatch contra render target offscreen
+ readback para o pipeline de processamento) e termina em um dos dois
caminhos: implementação real, ou deliberação documentada (ADR).

## Critérios de Aceite

- [ ] CA-3.1 — Relatório do spike existe, dentro do timebox, com conclusão
      clara (viável/inviável), restrições técnicas encontradas e evidência
      (POC/testes).
- [ ] CA-3.2 — **Caminho A (viável):** o fluxo `fsr3fg` aplica frame
      generation real no processamento — mensurável: a saída contém quadros
      gerados além dos quadros fonte para o fps configurado (verificável por
      contagem de quadros do output e/ou log) (CA2).
- [ ] CA-3.3 — **Caminho B (inviável):** ADR documenta a impossibilidade
      técnica e a alternativa adotada, e esse ADR é **aprovado explicitamente
      pelo usuário** — sem aprovação do usuário esta story não pode ser
      aceita (exigência do CA2 da spec).
- [ ] CA-3.4 — Em qualquer dos caminhos, `fsr3fg` deixa de cair em RIFE de
      forma silenciosa: existe sinalização explícita (evento/log) quando o
      fallback para RIFE ocorre, consumível pela Story 04.
- [ ] CA-3.5 — O fluxo FSR3 (qualquer caminho) funciona sem componentes
      instalados por driver; apenas DLLs redistribuídas legítimas do bundle
      (FFX) são exigidas (CA3).
- [ ] CA-3.6 — Sem regressão: fluxo `rife` atual intacto; código native de
      `catra_fg` para playback não é removido (fora de escopo do plano).
- [ ] CA-3.7 — Testes existentes passam.

## Notas do PO

- O risco R1 da spec (FFX FG pode exigir swapchain, só playback) é o ponto
  central do spike; se confirmado, vale o Caminho B com ADR + aprovação do
  usuário + fallback notificado.
- O ADR do Caminho B deve ser apresentado ao usuário para aprovação ANTES da
  story ser dada como pronta (bloqueio de aceite, não formalidade).

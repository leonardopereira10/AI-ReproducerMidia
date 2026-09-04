# Story 04 — Eventos de fallback tipados no pipeline + UI de processamento com dedup

**Tipo:** dev
**Dependências:** Stories 01, 02 e 03 (esta story reporta os fallbacks criados/ajustados por elas)
**Spec principal:** `.agents/specs/non-amd-fsr-processing_plan.md` (Escopo 4; CA6)

## Descrição

A tela de processamento deve informar ao usuário quando o fluxo escolhido não
é o que está sendo executado, sem repetir mensagens. O pipeline passa a
emitir **eventos de fallback tipados** e a UI de processamento exibe **uma
mensagem por tipo por job**. Exemplos definidos na spec: "FSR4 indisponível →
usando FSR1", "Encoder AMD indisponível → usando x265", "FrameGen FSR3
indisponível → usando RIFE".

## Critérios de Aceite

- [ ] CA-4.1 — Os eventos de fallback são tipados, distinguindo no mínimo:
      upscale (FSR4→FSR1), encoder (AMF→hw→x265) e interpolação/frame
      generation (FSR3 FG→RIFE).
- [ ] CA-4.2 — A tela de processamento exibe os fallbacks ativos com mensagem
      clara indicando o fluxo escolhido vs. o fluxo efetivamente em uso.
- [ ] CA-4.3 — Cada tipo de fallback aparece **no máximo uma vez por job**
      (dedup): eventos repetidos por quadro/estágio não geram mensagens
      duplicadas (CA6).
- [ ] CA-4.4 — Job sem nenhum fallback não exibe mensagem de fallback.
- [ ] CA-4.5 — Fallbacks reais das Stories 01, 02 e 03 aparecem corretamente
      na UI (ex.: FFX indisponível → "usando FSR1"; AMF ausente → "usando
      x265"; `fsr3fg` caindo para RIFE → mensagem correspondente).
- [ ] CA-4.6 — Testes automatizados novos para os eventos de fallback
      (tipagem e dedup) (CA8).
- [ ] CA-4.7 — Testes existentes passam.

## Notas do PO

- O warning de FSR4 em Settings (Story 01) é coisa distinta: esta story trata
  apenas da **tela de processamento** durante a execução de um job.
- Mensagens devem ser informativas e curtas; não é tela de diagnóstico.

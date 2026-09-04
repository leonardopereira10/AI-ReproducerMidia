# Story 06 — QA: validação final end-to-end contra a spec

**Tipo:** qa
**Dependências:** Stories 01, 02, 03, 04 e 05
**Spec principal:** `.agents/specs/non-amd-fsr-processing_plan.md` (CA1–CA8 + spec original do usuário)

## Descrição

Verificação final ponta a ponta dos 8 critérios de aceite da spec aprovada,
contra a spec original do usuário (processamento aplica os algoritmos; frame
generation FSR corrigida; fluxos FSR1/FSR3 independentes de driver; fallbacks
informados sem repetição; melhor encoder/decoder em máquina AMD; máquina
não-AMD usa encoder de uso geral; FSR4 no ecossistema dev). Consolida as
evidências das stories anteriores e emite o relatório final de aceite.

## Critérios de Aceite

- [ ] CA-6.1 — Cada critério CA1–CA8 da spec tem status verificado com
      evidência reproduzível — admitindo as exceções previstas na própria
      spec: CA7 por causa raiz externa documentada; CA1/CA3/CA5 por simulação
      declarada (R5).
- [ ] CA-6.2 — Cenários E2E exercidos na máquina dev: fluxo `fsr4` (com
      ecossistema FSR4), fluxo `fsr1`, fluxo `fsr3fg` (FG real OU ADR
      aprovado pelo usuário + fallback notificado) e export em cenário não-AMD
      simulado com cascata de encoder.
- [ ] CA-6.3 — **CA6 verificado em E2E:** tela de processamento exibe os
      fallbacks ativos com no máximo uma mensagem por tipo por job.
- [ ] CA-6.4 — Todos os testes automatizados passam (CA8).
- [ ] CA-6.5 — Nenhum defeito blocker/alto aberto; defeitos menores
      remanescentes registrados como backlog com severidade.
- [ ] CA-6.6 — Relatório final existe contendo: resultado por CA, ambientes
      usados, validações reais vs. simuladas (R5), limitações conhecidas e
      pendências (ex.: validação em hardware NVIDIA/Intel real).

## Notas do PO

- Este é o gate de entrega do plano: só fechar quando o relatório cobrir
  todos os CAs e as pendências estiverem explicitamente declaradas.
- Se o Caminho B da Story 03 foi o adotado, conferir aqui se o ADR tem a
  aprovação do usuário registrada.

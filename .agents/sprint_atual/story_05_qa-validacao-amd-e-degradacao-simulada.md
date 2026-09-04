# Story 05 — QA: validação em máquina AMD (POC) + degradação simulada não-AMD

**Tipo:** qa
**Dependências:** Stories 01, 02 e 03 (validação das implementações)
**Spec principal:** `.agents/specs/non-amd-fsr-processing_plan.md` (CA1, CA3, CA4, CA5; estratégia R5)

## Descrição

Validação dos critérios dependentes de hardware usando a máquina atual (AMD,
POC) e, como não há máquina não-AMD disponível, **degradação simulada**
(estratégia R5 da spec): ocultar/renomear `amfrt64.dll`, remover as 8 DLLs
FFX do lado de `catra-gpu.dll`, forçar caminhos de fallback — além de rodar
os testes unitários da cascata. Tudo que for validado apenas por simulação
deve ser declarado explicitamente no relatório; validação em hardware
NVIDIA/Intel real fica pendente de máquina.

## Critérios de Aceite

- [ ] CA-5.1 — **CA4 (máquina AMD real):** processamento usa AMF encode +
      D3D11VA decode (evidência em log), sem regressão do fluxo atual.
- [ ] CA-5.2 — **CA1:** upscale FSR1 processa end-to-end com output correto
      (sem corrupção visual, resolução conforme configuração) — em GPU não-AMD
      real, OU via simulação com declaração explícita no relatório.
- [ ] CA-5.3 — **CA5 (cenário não-AMD simulado):** export completa com
      encoder de uso geral (libx265; NVENC/QSV se o ambiente dispuser) e o
      arquivo de saída é válido (reproduzível, codec/duração corretos).
- [ ] CA-5.4 — **CA3:** fluxos FSR1/FSR3 completam com `amfrt64.dll`
      indisponível (simulação), usando apenas DLLs redistribuídas legítimas
      do bundle.
- [ ] CA-5.5 — Todos os testes existentes + novos testes da cascata passam.
- [ ] CA-5.6 — Relatório de testes existe e declara, por critério (CA1, CA3,
      CA4, CA5), o que foi validado em hardware real vs. simulação, incluindo
      os passos de simulação executados.

## Notas do PO

- Qualquer falha descoberta volta como defeito para a story dev
  correspondente (01/02/03) — QA não corrige código nesta story.
- Validação em máquina NVIDIA/Intel real fica registrada como pendência
  (não bloqueia o aceite desta story, conforme R5).

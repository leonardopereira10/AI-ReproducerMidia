# Story 01 — Corrigir "FSR4 sempre em fallback" + disponibilidade real na UI

**Tipo:** dev
**Dependências:** nenhuma
**Spec principal:** `.agents/specs/non-amd-fsr-processing_plan.md` (Escopo 5+6; CA7)

## Descrição

Na máquina de desenvolvimento (RDNA4 com ecossistema FSR4 instalado), o app
sempre executa o fluxo de fallback FSR1 mesmo com FSR4 disponível. É preciso
diagnosticar com evidência, documentar a causa raiz e corrigir. Hipótese forte
do recon: `catra-gpu.dll` copiado para a raiz do bin sem as 8 DLLs FFX ao
lado (elas só existem em `runtimes/win-x64/native/`); `FfxRuntime` resolve o
ModuleDir a partir do DLL carregado e o probe das DLLs falha —
`SetDllDirectory(lib/ffmpeg)` no App também pode interferir.

No mesmo escopo (Escopo 6): `SettingsViewModel.Fsr4Available` nunca recebe o
valor real (default hardcoded `true`) — a disponibilidade de FSR4 e de FG deve
ser injetada da probe nativa real e o warning de indisponibilidade deve se
tornar funcional.

## Critérios de Aceite

- [ ] CA-1.1 — Diagnóstico registrado com evidência (log/repro) apontando onde
      a probe FFX falha (load do loader, probe das 8 DLLs, adapter DX12 ou
      criação do contexto de teste).
- [ ] CA-1.2 — Causa raiz documentada (relato na própria story ou em `docs/`).
- [ ] CA-1.3 — Na máquina dev com ecossistema FSR4, processar com fluxo
      `fsr4` executa o caminho FSR4 real (verificável por log/probe) — OU a
      causa raiz é documentada como externa (comportamento do loader FFX/AGS
      fora do controle do projeto), conforme admite o CA7 da spec.
- [ ] CA-1.4 — `Fsr4Available` na UI de Settings reflete o resultado real da
      probe nativa (`catra_is_fsr4_available`), não mais default hardcoded;
      idem para availability de FG.
- [ ] CA-1.5 — O warning de "FSR 4 não disponível" é exibido quando a probe
      retorna indisponível e ausente quando disponível.
- [ ] CA-1.6 — O fallback FSR4→FSR1 continua funcionando quando FSR4 é
      genuinamente indisponível (sem regressão da cascata existente).
- [ ] CA-1.7 — Testes existentes passam.

## Notas do PO

- O CA7 da spec admite explicitamente "causa raiz documentada se for externa"
  (risco R3) — não bloquear a story se a evidência apontar para o loader
  FFX/AGS; nesse caso a correção vira documentação + decisão registrada.
- Não remover nem reescrever `ffx_runtime.cpp` sem evidência do diagnóstico;
  a fix deve atacar a causa apontada pelo log.

# Story 06 — QA/validação final da integração FSR + documentação

**Sprint:** SPRINT_04 · **Spec/Plano:** `.agents/specs/fsr-sdk-integration_plan.md` (aprovado 2026-08-13)
**Tipo:** qa

## Descrição

Validação ponta a ponta da integração FidelityFX contra **todos** os critérios de aceite
do plano aprovado, com foco nos critérios já ajustados pelo PO (A1–A7), e produção da
documentação final. Inclui execução da matriz completa de cenários e registro de
evidências (logs, medições, prints quando aplicável).

**Matriz de validação:**
- Runtime com DLLs FFX + GPU AMD / runtime sem DLLs (fallback) / GPU não-RDNA4 (fallback FSR 3.1).
- Playback: {FSR1, FSR4, off} × {FG on, FG off} — incluindo medições A4/A5 no par FSR1+FG e FSR4+FG.
- Export offline: FSR 1 (perfil DLNA) e FSR 4 → .mp4 válido.
- Falhas induzidas: DLL ausente/corrompida, criação de FG swapchain falhando, resize de janela durante FG.

**Documentação:** `docs/FSR_FFX_INTEGRATION.md` — arquitetura (loader runtime, módulos
`ffx_runtime`/`catra_fg_*`, contorno AMF), **limitação zero-MV** do upscale temporal para
vídeo (ghosting, FSR 1 como default de export), licenciamento MIT/copyright AMD (A7),
procedimentos de deploy das DLLs e como reproduzir as medições A4/A5.

## Critérios de Aceite

- [ ] Todos os critérios de aceite do plano verificados com evidência, em especial:
      - **A2:** export FSR 1 (DLNA) entrega .mp4 válido; export FSR 4 entrega .mp4 válido.
      - **A4/A5:** clipe 30 fps + FG on → presents ≥1,5× frames decodificados (~2×) **e** drift A/V ≤ ±40 ms; falha de criação do swapchain FG → fallback automático sem crash.
      - **A6:** matriz upscale×FG simultâneo validada (default FSR1+FG).
      - **A1:** as 8 DLLs presentes na saída e carregando (smoke test).
      - **A7:** atribuição MIT junto a headers/DLLs.
- [ ] Build `dotnet build CATRA.sln` + nativo sem SDK FSR em build-time; app sem DLLs FFX
      funciona com fallback sem crash.
- [ ] `dotnet test` completo passa; smoke test nativo passa; sem regressões.
- [ ] Cenários de falha (DLL ausente, swapchain fail, resize) sem crash e com UX degradada correta.
- [ ] `docs/FSR_FFX_INTEGRATION.md` criada/atualizada cobrindo arquitetura, limitação
      zero-MV, deploy, licenciamento e procedimento de medição A4/A5.
- [ ] Relatório de QA com resultados da matriz e medições anexado ao reporte da sprint.

## Dependências

- **Stories 01, 02, 03, 04 e 05** concluídas.

## Notas

- Hardware: cenários completos exigem GPU AMD (idealmente RDNA 4 para FSR 4 ML); em outras
  GPUs registrar explicitamente o comportamento de fallback observado (FSR 3.1 / indisponível).
- Não alterar código nesta story — defeitos encontrados voltam como bugfix no sprint
  (mesmo padrão dos `bugfix_*.json` existentes), sem tocar nos já abertos.

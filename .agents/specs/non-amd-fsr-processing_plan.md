# Plano de Execução — Processamento FSR1/FSR3 independente de GPU AMD

> Spec do usuário (verbatim no final). Recon técnico: `.pi-subagents/artifacts/outputs/6167d934/context.md`

## Objetivo

O pipeline de **processamento/export** do CATRA deve funcionar em máquinas
**sem GPU AMD**, aplicando os fluxos **FSR1 (upscale)** e **FSR3 (frame
generation)** independentemente de drivers vendor-específicos. Em máquinas AMD
deve usar o melhor encoder/decoder disponível. A tela de processamento deve
informar fallbacks ativos (sem repetição).

## Contexto técnico (recon)

- **FSR1 upscale**: já vendor-neutro (D3D11 compute); fallback FSR4→FSR1 existe
  em 2 níveis (native `ResolveUpscaleMethod` + C# `CreateUpscalerWithFallback`).
- **FSR3 FG no processamento**: NÃO funciona — `"fsr3fg"` mapeia para method 2
  e o native trata qualquer method ≠ RIFE como indisponível, caindo para RIFE
  **silenciosamente** (`interp_rife.cpp:1028-1031`). O contexto FG (`catra_fg`)
  existe só para playback e não é chamado pelo C#.
- **Encoder AMF-only**: `ProcessingPipeline.cs:267` cria encoder sem fallback.
  Sem `amfrt64.dll` (driver Adrenalin) ou em device não-AMD →
  `CATRA_ERR_DEVICE` → **job inteiro falha**. Único hard blocker de export.
- **Decode**: D3D11VA→software já é vendor-neutro com cascata funcional.
- **Bug FSR4 no dev (RDNA4)**: app sempre roda fallback. Hipótese forte:
  `catra-gpu.dll` no raiz do bin sem as 8 DLLs FFX ao lado (só existem em
  `runtimes/win-x64/native/`); `FfxRuntime` resolve ModuleDir do DLL carregado
  e o probe de DLLs falha. `SetDllDirectory(lib/ffmpeg)` no App também pode
  interferir. Requer diagnóstico com evidência.
- **UX**: `SettingsViewModel.Fsr4Available` nunca recebe valor real; warnings
  nunca aparecem; label `fsr3fg` enganosa.

## Escopo

1. **Spike FSR3 FG offline**: viabilidade de aplicar Frame Generation FFX
   fora do swapchain de playback (contexto FG + dispatch contra render target
   offscreen, readback para o pipeline). Timeboxed.
2. **FSR3 FG no pipeline** (se spike viável): aplicar FG de verdade no
   processamento; se inviável, decidir e documentar alternativa e garantir
   notificação honesta de fallback.
3. **Encoder com fallback**: cascata AMF → FFmpeg hardware (autodetect
   NVENC/QSV/AMF via hwaccel) → libx265 software. Decode mantém D3D11VA
   genérico + fallback software (já funciona).
4. **Notificação de fallbacks na UI de processamento**: pipeline reporta
   eventos de fallback tipados; UI exibe UMA mensagem por tipo (dedup), ex.:
   "FSR4 indisponível → usando FSR1", "Encoder AMD indisponível → usando
   x265", "FrameGen FSR3 indisponível → usando RIFE".
   Nota de implementação (PO): a cascata de encoder deve tratar tanto
   `CATRA_ERR_DEVICE` (driver ausente/GPU não-AMD) quanto `CATRA_ERR_NOT_IMPL`
   (build sem `CATRA_AMF_ROOT`) como gatilhos de fallback.
5. **Fix FSR4 sempre em fallback (POC na máquina atual, RDNA4)**: diagnóstico
   com log, causa raiz documentada, correção.
6. **Disponibilidade real na UI**: `Fsr4Available`/FG availability injetados
   da probe nativa; warnings funcionais.

## Fora de Escopo

- Frame generation em playback (integração `catra_fg` no player).
- Alterar algoritmo RIFE além da sinalização de fallback.
- Remover código native existente (`catra_fg` playback permanece).
- Stubs do backlog original (legendas, TMDB, bluetooth etc.).

## Critérios de Aceite

- [ ] CA1 — Upscale FSR1 processa end-to-end em GPU não-AMD com output correto
      (POC NVIDIA ou Intel).
- [ ] CA2 — Fluxo `fsr3fg` aplica frame generation real no processamento, OU
      impossibilidade técnica documentada (ADR) **aprovado pelo usuário** com
      fallback notificado.
- [ ] CA3 — Fluxos FSR1/FSR3 funcionam sem componentes instalados por driver
      (Adrenalin/`amfrt64.dll`); DLLs redistribuídas no bundle (FFX/AGS) são
      legítimas e permitidas.
- [ ] CA4 — Máquina AMD (POC máquina atual): melhor encoder/decoder usado
      (AMF encode + D3D11VA decode) — sem regressão do fluxo atual.
- [ ] CA5 — Máquina não-AMD: export completa com encoder de uso geral
      (x265 software; NVENC/QSV quando disponível).
- [ ] CA6 — Tela de processamento exibe fallbacks ativos; cada tipo de
      fallback aparece no máximo uma vez por job.
- [ ] CA7 — Máquina dev com ecossistema FSR4: fluxo FSR4 executa (bug do
      fallback permanente corrigido) ou causa raiz documentada se for externa.
- [ ] CA8 — Testes existentes passam; novos testes para cascata de encoder e
      eventos de fallback.

## Riscos

- **R1**: FFX FrameGeneration pode exigir swapchain (apenas playback) →
  contingência: ADR + fallback notificado (coberto por CA2/CA6).
- **R2**: Refactor do encoder pode regredir caminho AMF atual → mitiga com POC
  na máquina AMD (CA4) antes do merge.
- **R3**: Causa do bug FSR4 pode ser comportamento do loader FFX/AGS fora do
  nosso controle → documentar (CA7 admite isso).
- **R4**: NVENC/QSV exigem redistribuição de libs FFmpeg com hwaccel →
  fallback software garante CA5 de qualquer forma. (PO: `lib/ffmpeg/avcodec-63.dll`
  já contém libx265, hevc_nvenc, hevc_qsv e hevc_amf — cascata viável com o
  binário vendorizado.)
- **R5**: Nenhuma máquina não-AMD disponível para POC → estratégia de validação
  de CA1/CA3/CA5: degradação simulada na máquina atual (ocultar `amfrt64.dll`,
  remover as 8 DLLs FFX, forçar caminhos) + testes unitários da cascata. O que
  for validado só por simulação deve ser declarado explicitamente no relatório
  final; validação em hardware NVIDIA/Intel real fica pendente de máquina.

## Spec original do usuário (verbatim)

> * apenas meu processamento deve aplicar os algoritimos nos videos
> * corrija a geração de quadros fsr
> * fluxo fsr1 e fsr3 devem funcionar independente de driver disponivel
> * caso fluxo selecionado não estiver sendo executado corretamente a tela de
>   processamento deve informar os fallbacks que estão sendo aplicados (sem
>   repetir mensagens, apenas para saber que não está executando o que
>   escolheu)
> * Em maquinas AMD deve usar o melhor encoder/decoder disponivel (pode usar o
>   computador atual para POC), em maquinas não AMD pode usar qualquer
>   encoder/decoder de uso geral
> * meu computador possui o ecosistema para FSR4 mas o app sempre executa nos
>   fluxos de fallback

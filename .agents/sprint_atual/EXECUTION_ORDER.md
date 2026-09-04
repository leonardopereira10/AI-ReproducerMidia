# Ordem de Execução — Sprint `non-amd-fsr-processing`

> Validação do PO sobre as 6 subtasks de `.agents/sprint_atual/` contra o plano
> `.agents/specs/non-amd-fsr-processing_plan.md` (CA1–CA8).
> Restrições: máximo **2 subtasks paralelas**; dev antes de QA; subtask 03
> (spike FG) inicia cedo porque o GO/NO-GO afeta decisões das demais;
> subtask 04 depende de 01/02/03; QA 05 após dev; QA 06 por último.

## Tabela de Ordem

| Ordem | Subtask | Agente | Dependência |
|-------|---------|--------|-------------|
| 1 (paralelo, slot A) | `subtask_03_fsr3_fg_offline.md` — fase spike (timebox 8h) | developer-critical | Nenhuma |
| 1 (paralelo, slot B) | `subtask_01_fix_fsr4_fallback.md` | developer-alta | Nenhuma |
| 2 (paralelo, slot A) | `subtask_02_cascata_encoder.md` | developer-alta | Nenhuma formal; **iniciar após a 01 concluir** (evita conflito em `catra_gpu.h/.cpp`, `INativeBridge.cs`, `NativeBridge.cs`) |
| 2 (paralelo, slot B) | `subtask_03_fsr3_fg_offline.md` — fase pós-spike (Caminho A implementação OU Caminho B ADR + fallback sinalizado) | developer-critical | Fase spike própria; decisão GO/NO-GO registrada |
| 3 | `subtask_04_eventos_fallback_ui.md` | developer-media | **01, 02 e 03 concluídas** (consome os eventos reais das três) |
| 4 | `subtask_05_qa_amd_degradacao.md` | qa-tester-alta | 01, 02 e 03 obrigatórias + **04 concluída** (para não deixar validação de mensagens UI como BLOCKED) |
| 5 | `subtask_06_qa_final_e2e.md` | qa-tester-alta | **Todas**: 01, 02, 03, 04 e 05 completas |

## Fases e Paralelismo Permitido

- **Fase 1 — 2 paralelos:** subtask 03 (spike) ∥ subtask 01.
  - 03 abre cedo: o relatório GO/NO-GO do spike destrava decisões de 04
    (contrato do evento `FrameGen`/`Interp`) e dos cenários S5/E3 de QA.
  - 01 é independente (diagnóstico FFX + availability na UI de Settings).
  - Conflito de baixo risco nesta fase: o spike toca só `tools/` (read-only
    no resto); a 01 toca `ffx_runtime.cpp`/`upscale_fsr4.cpp`/UI.
- **Fase 2 — 2 paralelos:** subtask 02 ∥ continuação da subtask 03.
  - 02 só inicia quando a 01 liberar o slot **e** a superfície ABI
    (`catra_gpu.h`) estiver estável, pois ambas adicionam entry points novos
    (`catra_texture_readback_bgra` vs. `catra_fg_offline_*`).
  - Atenção: 02 e 03-GO tocam regiões **distintas** de `ProcessingPipeline.cs`
    (encoder ~linha 267 vs. interp `CreateInterpolation`/`MapInterpMethod`
    ~215-234/840-845). Manter merges frequentes; em caso de conflito de
    merge não trivial, serializar (02 primeiro — remove o único hard
    blocker de export).
- **Fase 3 — isolada:** subtask 04. Toca `ProcessingPipeline.cs` (3 pontos
  de emissão), Core, QueueService, ViewModel e XAML — só roda depois que
  01/02/03 assentarem o código, evitando retrabalho e conflito.
- **Fase 4 — QA intermediário:** subtask 05. QA não corrige código; defeitos
  voltam às stories dev (01/02/03/04) via orquestrador. Se defeito
  blocker/alto surgir, a correção ocupa o slot dev e a 06 aguarda.
- **Fase 5 — gate final:** subtask 06, sempre por último.

**Regra geral de paralelismo:** nunca mais que 2 subtasks simultâneas;
QA 05 e QA 06 **nunca** rodam em paralelo entre si nem em paralelo com dev
que altere código sob teste (contamina evidência).

## Cobertura dos CAs do Plano

| CA | Coberto por |
|----|-------------|
| CA1 (FSR1 E2E não-AMD) | Já vendor-neutro; validado por 05/06 (simulação R5) |
| CA2 (fsr3FG real OU ADR) | 03; verificação final 06 |
| CA3 (sem driver Adrenalin) | 02 + 03; validado por 05/06 (S1–S5 / E5–E6) |
| CA4 (AMD usa AMF + D3D11VA) | 02 preserva caminho AMF; validado por 05/06 |
| CA5 (não-AMD exporta c/ encoder geral) | 02; validado por 05/06 |
| CA6 (fallbacks na UI, dedup) | 04; validado por 05/06 |
| CA7 (FSR4 no ecossistema dev) | 01; validado por 05/06 |
| CA8 (testes) | Testes novos em 01/02/03/04; gate em 05/06 |

## Notas de Coordenação (ajustes exigidos pelo PO)

1. **Readback GPU→CPU da subtask 02 — ACEITÁVEL.** O readback BGRA só é
   exercido no caminho de fallback (FFmpeg CLI); o caminho AMF permanece
   zero-copy e inalterado. Overhead estimado (~1–2 ms/frame 1080p) é
   aceitável em fallback. **Diretriz:** entregar primeiro o MVP
   (AMF → libx265 direto), depois estender para NVENC/QSV; e garantir
   leitura assíncrona do stdout do FFmpeg para evitar deadlock de pipe.
   Risco residual: throughput em 4K via readback — declarar no relatório QA.
2. **Sonda de availability FG (01 vs 03):** `catra_is_fg_available()` já
   existe no ABI mas depende de `catra_init`. A sonda leve independente de
   init (padrão `catra_is_ffx_available()`) fica **sob propriedade da 03**
   (Caminho A) — a 01 restringe-se à availability de FSR4. Evita duplicar
   entry point no ABI entre agentes paralelos.
3. **Contrato de eventos (02/03 → 04):** a 04 é dona do modelo unificado
   `FallbackEvent(Type, Requested, Effective, Message, JobId)` e deve
   **adaptar** o `EncoderFallbackEventArgs` da 02 e o evento
   `fsr3fg→RIFE` da 03 (não apenas "preparar ponto/TODO" — como 04 roda
   depois, a integração é real). Formato do evento de FG/interp deve ser
   acordado já na decisão GO/NO-GO do spike.
4. **Segurança de simulação (06 alinhado com 05):** a subtask 06 **NÃO**
   renomeia/oculta `amfrt64.dll` do sistema (cenário E5 como escrito).
   Deve reutilizar a técnica sandbox da 05: cópia do bin + stub inválido de
   `amfrt64.dll` na raiz da sandbox. Nunca tocar driver do SO.
5. **Caminho B (NO-GO) da 03:** a aprovação do ADR pelo usuário é bloqueio
   de aceite. Submeter via orquestrador **imediatamente** após o relatório
   do spike para não atrasar 04/05; o contrato do evento de fallback
   sinalizado (CA-3.4) pode ser acordado junto com a decisão, minimizando
   bloqueio.
6. **Padronização:** agente QA das subtasks 05/06 tratado como
   `qa-tester-alta` (a subtask 06 grafava `qa-tester-alto`).

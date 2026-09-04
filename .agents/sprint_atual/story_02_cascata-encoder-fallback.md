# Story 02 — Cascata de encoder com fallback (AMF → FFmpeg hardware → x265 software)

**Tipo:** dev
**Dependências:** nenhuma (pode rodar em paralelo com a Story 01)
**Spec principal:** `.agents/specs/non-amd-fsr-processing_plan.md` (Escopo 3; CA3, CA4, CA5; testes do CA8)

## Descrição

Hoje o pipeline cria o encoder AMF sem fallback
(`src/CATRA.Services/Processing/ProcessingPipeline.cs:267`): sem
`amfrt64.dll` (driver Adrenalin), em GPU não-AMD, ou em build sem
`CATRA_AMF_ROOT`, o job inteiro de export falha — é o único hard blocker para
o app funcionar end-to-end fora de máquina AMD. Implementar a cascata:
**AMF → encoder de hardware via FFmpeg (autodetect NVENC/QSV/AMF via
hwaccel) → libx265 software**. Decode fica fora do escopo (cascata
D3D11VA→software já funciona).

**Nota PO (gatilhos de fallback):** a cascata deve tratar tanto
`CATRA_ERR_DEVICE` (driver ausente / GPU não-AMD) quanto
`CATRA_ERR_NOT_IMPL` (build compilado sem `CATRA_AMF_ROOT`) como gatilhos
para o próximo encoder da cascata — nenhum dos dois pode derrubar o job.

**Nota PO (risco R4):** `lib/ffmpeg/avcodec-63.dll` já vendorizado contém
libx265, hevc_nvenc, hevc_qsv e hevc_amf — a cascata é viável com o binário
FFmpeg atual, sem novo redistribuível.

## Critérios de Aceite

- [ ] CA-2.1 — Em máquina AMD com Adrenalin, AMF continua sendo selecionado e
      usado, sem regressão do fluxo atual (CA4; seleção verificável no log do
      job).
- [ ] CA-2.2 — Falha do AMF com `CATRA_ERR_DEVICE` **ou** `CATRA_ERR_NOT_IMPL`
      dispara o fallback para o próximo encoder da cascata (job não falha).
- [ ] CA-2.3 — Export completa com libx265 software como último fallback em
      cenário sem AMF e sem encoder de hardware disponível (CA5), com saída
      HEVC válida.
- [ ] CA-2.4 — Os fluxos funcionam sem componentes instalados por driver
      (`amfrt64.dll`); apenas DLLs redistribuídas legítimas no bundle (CA3).
- [ ] CA-2.5 — O encoder efetivamente usado e o motivo da seleção/fallback
      ficam registrados no log do job.
- [ ] CA-2.6 — Testes automatizados novos cobrindo a cascata de encoder
      (gatilhos DEVICE e NOT_IMPL, ordem de fallback) (CA8).
- [ ] CA-2.7 — Testes existentes passam.

## Notas do PO

- Validação em máquina AMD real (CA4) e em cenário não-AMD simulado (CA5)
  será feita pela Story 05 (QA); aqui basta garantir o comportamento
  observável por log e testes.

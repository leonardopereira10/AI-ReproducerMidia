# Subtask 05 — QA: validação em máquina AMD (POC) + degradação simulada não-AMD

| Campo       | Valor                                                |
|-------------|------------------------------------------------------|
| **Story**   | Story 05 — QA: validação em máquina AMD (POC) + degradação simulada não-AMD |
| **Tipo**    | qa                                                   |
| **Complexidade** | alta (E2E multi-layer: native C++ ABI ↔ P/Invoke ↔ pipeline ↔ UI; hardware real + manipulação de ambiente; cascatas de fallback cross-layer) |
| **Agente**  | qa-tester-alta                                       |
| **Dependências** | Subtasks 01, 02 e 03 concluídas (obrigatório); subtask 04 recomendada (validação das mensagens de fallback na UI) |

## Descrição

Validação E2E dos critérios dependentes de hardware da spec
`.agents/specs/non-amd-fsr-processing_plan.md` (CA1, CA3, CA4, CA5;
estratégia R5). Duas frentes:

1. **Máquina AMD atual (hardware real, POC)**: confirmar que o fluxo
   FSR4/FSR1 roda, que AMF encode + D3D11VA decode continuam selecionados
   (spec CA4) e que o bug "FSR4 sempre em fallback" foi resolvido
   (Story 01, CA7/CA-1.3) — sem regressão do fluxo atual.
2. **Degradação simulada de máquina não-AMD** (estratégia R5): como não há
   máquina NVIDIA/Intel disponível, simular a ausência dos componentes
   AMD — ocultar `amfrt64.dll`, remover as 8 DLLs FFX, forçar settings —
   e validar as cascatas de fallback (Stories 02/03) e as mensagens de
   fallback (Story 04). Tudo que for validado só por simulação deve ser
   **declarado explicitamente no relatório**; validação em hardware
   NVIDIA/Intel real fica registrada como pendência (não bloqueia aceite).

### Contexto técnico (investigação read-only)

**Build e binários (verificado):**
- Build completo: `.\build_native_now.bat` (repo root) — chama
  `scripts\build-native.ps1` com `-VcpkgRoot C:\Projetos\vcpkg`,
  `-OnnxRuntimeRoot lib\onnxruntime\...`, `-AmfRoot lib\amf`
  (com `-AmfRoot` → `CATRA_HAS_AMF` ON; sem ele → encoder AMF vira
  `CATRA_ERR_NOT_IMPL`, útil para simulação da branch -2).
- .NET: `dotnet build CATRA.sln -c Debug` (BeforeBuild roda
  `build-native-vs.bat` automaticamente).
- App: `src/CATRA.App/bin/Debug/net8.0-windows/CATRA.App.exe`.
- Deploy nativo (verificado em disco): `catra-gpu.dll` existe TANTO na
  raiz do bin QUANTO em `src/CATRA.App/bin/Debug/net8.0-windows/runtimes/win-x64/native/`;
  as 8 DLLs FFX (`amd_fidelityfx_loader_dx12.dll`,
  `amd_fidelityfx_upscaler_dx12.dll`,
  `amd_fidelityfx_framegeneration_dx12.dll`, `amd_ags_x64.dll`,
  `amd_acs_x64.dll`, `D3D12Core.dll`, `dxcompiler.dll`, `dxil.dll`) +
  `onnxruntime.dll` estão **apenas** em `runtimes/win-x64/native/`.
  Fonte de instalação: `native/catra-gpu/install/runtimes/win-x64/native/`.
  → Qualquer simulação de remoção de DLLs deve cobrir **os dois locais**.
- Logs do app: `%LOCALAPPDATA%\CATRA\logs\catra-yyyyMMdd.log`
  (`DiagnosticsLogger`, `src/CATRA.Core/Library/DiagnosticsLogger.cs:142`)
  + Trace console (`App.xaml.cs:51`).
- Smoke tests nativos (úteis como evidência isolada por camada):
  `ffx_load_smoke_test`, `fg_smoke_test`, `interop_readback_test`
  (build em `native/catra-gpu/build/`).

**Testes automatizados (verificado):**
- Projetos: `tests/CATRA.Core.Tests`, `CATRA.Data.Tests`,
  `CATRA.Services.Tests`, `CATRA.UI.Tests` (xUnit).
- Relevantes: `tests/CATRA.Services.Tests/Processing/`
  (`EncoderFallbackFactoryTests.cs` — cascata Story 02,
  `ProcessingPipelineTests.cs`, `NativeBridgeTests.cs`,
  `AudioMuxerIntegrationTests.cs`) e
  `tests/CATRA.UI.Tests/ProcessingQueueViewModelTests.cs` (dedup Story 04).
- Rodar: `dotnet test CATRA.sln` (ou por projeto).

**Pontos de fallback sob teste (do recon `.pi-subagents/artifacts/outputs/6167d934/context.md`):**
- Upscale: native `ResolveUpscaleMethod` (FSR4→FSR1) +
  C# `CreateUpscalerWithFallback` (`ProcessingPipeline.cs:862-882`).
- Encoder: cascata AMF → FFmpeg hw (NVENC→QSV) → libx265
  (`EncoderFallbackFactory`, Story 02). Gatilhos: `CATRA_ERR_DEVICE` (-4,
  sem `amfrt64.dll` / device não-AMD) e `CATRA_ERR_NOT_IMPL` (-2, build
  sem `CATRA_AMF_ROOT`).
- Interp/FG: fluxo `fsr3fg` — resultado da Story 03 (FG real offline OU
  fallback RIFE com sinalização explícita; caminho B exige ADR aprovado).
- UI: eventos `FallbackEvent` tipados, dedup 1 mensagem/tipo/job (Story 04).
- Decode: cascata D3D11VA → transfer → software (já vendor-neutra).

### Regras de execução

- **QA NÃO corrige código nesta story.** Qualquer falha vira defeito
  (JSON schema em `.agents/project/testing-conventions.md`) entregue via
  orquestrador à story dev correspondente (01/02/03/04).
- **NUNCA tocar `amfrt64.dll` do sistema** (System32/driver Adrenalin) —
  a simulação é feita por sandbox/stub, nunca por renomear driver do SO.
- Toda simulação em **cópia sandbox** do bin
  (ex.: `src/CATRA.App/bin/Debug/net8.0-windows-nonamd-sandbox/`),
  restaurando/registrando o estado original ao final.
- **Build gate obrigatório** (skill build-gate) antes de marcar a subtask
  como completed: build completo + `dotnet test` verdes.

## Arquivos Alvo (fileScope)

QA não modifica código-fonte. Escopo de escrita:

| Arquivo | Ação |
|---------|------|
| `.agents/sprint_atual/qa_report_story_05_amd_degradacao.md` | **NOVO** — relatório final (formato na seção Passos, Passo 5) |
| `.agents/sprint_atual/sprint_defect_<n>.json` | **NOVO(s)** — um por defeito encontrado (schema em testing-conventions.md) |
| `src/CATRA.App/bin/Debug/net8.0-windows-nonamd-sandbox/` | **TEMPORÁRIO** — cópia sandbox do bin para simulações (removível ao final) |

Escopo de leitura (evidências): logs em `%LOCALAPPDATA%\CATRA\logs\`,
saídas de export do app, `tests/`, `native/catra-gpu/build/` (smoke tools).

## Passos

### Passo 0 — Preparação e build gate inicial

1. `.\build_native_now.bat` → build nativo deve terminar com exit code 0.
2. `dotnet build CATRA.sln -c Debug` → 0 erros.
3. `dotnet test CATRA.sln` → baseline: todos os testes existentes passam
   (incluindo novos testes das subtasks 01–04, ex.:
   `EncoderFallbackFactoryTests`). Registrar contagem (passed/failed/skipped).
4. Conferir deploy: `catra-gpu.dll` + 8 DLLs FFX presentes em
   `bin/Debug/net8.0-windows/runtimes/win-x64/native/`. Se ausentes,
   registrar como defeito (deployment) e tratar como bloqueio de evidência.
5. Preparar mídia de teste: 1 vídeo curto (10–30 s, H.264 ou HEVC,
   resolução conhecida) para todos os cenários — mesma entrada em todos os
   fluxos para comparação A/B.

### Passo 1 — Máquina AMD real (POC) — espec CA4 / CA-5.1 + CA7

1. Rodar `CATRA.App.exe` do bin padrão (sem modificações).
2. **Fluxo FSR4**: configurar Settings → Upscale `fsr4`, interp `rife`;
   enfileirar o vídeo de teste; processar.
   - Evidência esperada no log: probe FFX OK, caminho FSR4 real executado
     (sem downgrade para FSR1) — valida fix da Story 01 (CA-1.3/CA7).
     Se o downgrade ocorrer, coletar evidência e registrar defeito Story 01.
   - Verificar UI Settings: `Fsr4Available` reflete a probe real e o
     warning de "FSR 4 não disponível" está AUSENTE (Story 01 CA-1.4/1.5).
3. **Encoder AMF**: evidência no log `Encoder selected: AMF`
   (Story 02 CA-2.1) + decode D3D11VA ativo (sem queda para software).
4. **Saída**: arquivo de export existe e é válido — reproduzível,
   codec HEVC (AMF), resolução = fonte × upscale configurado, duração ≈ fonte.
5. **Fluxo baseline**: processar o mesmo vídeo com Upscale `fsr1` — deve
   funcionar igual ou melhor que antes (sem regressão, spec CA4).
6. Registrar no relatório como **HARDWARE REAL**.

### Passo 2 — Degradação simulada não-AMD (estratégia R5) — CA1/CA3/CA5

Criar sandbox: copiar `bin/Debug/net8.0-windows/` inteiro para
`bin/Debug/net8.0-windows-nonamd-sandbox/`. Executar o app **da sandbox**
em cada cenário. Restaurar/não reutilizar a sandbox entre cenários.

1. **Cenário S1 — sem `amfrt64.dll` (branch ERR_DEVICE −4):**
   - Técnica: colocar um `amfrt64.dll` **stub inválido** (arquivo com
     imagem PE corrompida/vazia) na raiz da sandbox — o diretório do exe
     precede System32 na busca de DLL; `LoadLibraryW` falha →
     `CATRA_ERR_DEVICE`, mesma branch de DLL ausente
     (`encode_amf.cpp:409-419`). **Não renomear/mover o driver real.**
   - Rodar export (upscale fsr1, interp rife).
   - Esperado: cascata Story 02 dispara → encoder selecionado NVENC/QSV
     (se existir no ambiente) ou **libx265**; job COMPLETA; saída HEVC
     válida e reproduzível (CA-5.3/CA5); evento de fallback de encoder
     com `Reason` no log e mensagem única na UI (Story 04).
2. **Cenário S2 — build sem AMF (branch ERR_NOT_IMPL −2):**
   - Alternativa/complemento ao S1 sem manipular DLL: rebuild nativo via
     `scripts\build-native.ps1` SEM `-AmfRoot` → stub `CATRA_ERR_NOT_IMPL`;
     repetir deploy na sandbox e re-rodar o export.
   - Esperado: mesma cascata (gatilho −2), job completa (Story 02 CA-2.2).
3. **Cenário S3 — sem as 8 DLLs FFX:**
   - Remover da sandbox as 8 DLLs FFX em **ambos** os locais
     (`runtimes/win-x64/native/` e raiz, se aplicável).
   - Rodar export com Settings → Upscale `fsr4` (forçar o caminho que
     depende de FFX).
   - Esperado: `FfxRuntime::IsAvailable` = false → downgrade nativo
     FSR4→FSR1 + retry C# (`ProcessingPipeline.cs:862-882`); job completa
     com FSR1 (CA-5.2 via simulação); mensagem "FSR4 indisponível →
     usando FSR1" exibida UMA vez no job (dedup Story 04); UI Settings
     mostra `Fsr4Available=false` + warning.
   - Validar também `catra_is_fg_available` = false com FFX removido e
     `fg_smoke_test`/`ffx_load_smoke_test` reportando indisponibilidade
     graciosa (sem crash).
4. **Cenário S4 — combinado (pior caso não-AMD):**
   - S1 (stub amfrt64) + S3 (sem FFX), upscale `fsr4`, interp `rife`.
   - Esperado: FSR1 + encoder de uso geral; job completa; duas mensagens
     de fallback distintas (upscale + encoder), uma por tipo.
5. **Cenário S5 — fluxo `fsr3fg` degradado:**
   - Repetir S3/S4 com interp `fsr3fg`.
   - Esperado conforme resultado da Story 03: FG real offline (contagem
     de quadros da saída > quadros fonte, CA2) OU fallback RIFE com
     sinalização explícita (evento/log — CA-3.4), nunca RIFE silencioso.
6. **CA3 — legitimidade do bundle:** em S3/S4, confirmar que o app só
   exige DLLs redistribuídas do bundle (FFX/AGS em
   `runtimes/win-x64/native/`) — nenhum componente instalado por driver
   Adrenalin é requerido pelos fluxos FSR1/FSR3. Listar no relatório as
   DLLs efetivamente carregadas/usadas.
7. Registrar cada cenário no relatório como **SIMULAÇÃO**, com passos de
   simulação executados. Pendência declarada: comportamento em GPU
   NVIDIA/Intel real (AGS em não-AMD, NVENC/QSV, FL12_0 em iGPU antiga)
   — não bloqueia aceite (R5).

### Passo 3 — Testes automatizados (CA-5.5)

1. `dotnet test CATRA.sln` completo após os cenários manuais.
2. Verificar especificamente:
   - `EncoderFallbackFactoryTests` — gatilhos −4/−2, ordem de cascata,
     eventos, códigos que NÃO fazem fallback.
   - `ProcessingPipelineTests` — fallback de encoder no pipeline
     (`FakeNativeBridge.ThrowOnCreateEncoder`).
   - `ProcessingQueueViewModelTests` — dedup de mensagens por tipo/job.
3. Qualquer teste falhando → defeito à story dona do teste.

### Passo 4 — Edge cases e regressão transversal

1. Job com fallback + segundo job em sequência: mensagens de dedup não
   vazam entre jobs (1 por tipo POR job).
2. Cancelar job no meio do fallback de encoder: sem processo FFmpeg
   zombie, contexts liberados (verificar em log/gerenciador).
3. Alternar settings fsr4↔fsr1 com FFX removido: UI não entra em estado
   inconsistente (warning coerente).
4. Decode: fonte sem suporte hw (ou codec sem D3D11VA) → cascata para
   software decode continua funcional (já vendor-neutra — smoke).

### Passo 5 — Relatório (CA-5.6)

Escrever `.agents/sprint_atual/qa_report_story_05_amd_degradacao.md` com:

1. **Matriz por critério** — linhas: CA1, CA2 (conforme Story 03), CA3,
   CA4, CA5 (+ CA7 fix FSR4); colunas: status (PASSED/FAILED/BLOCKED),
   método (**HARDWARE REAL** | **SIMULAÇÃO** | **UNITÁRIO**), cenário
   (Passo/Cenário), evidência (excerto de log, path da saída, contagem
   de testes).
2. **Seção explícita "Hardware real vs Simulação"** — duas listas
   separadas, sem ambiguidade; tudo que não foi possível validar fica em
   "Pendências" (ex.: GPU NVIDIA/Intel física).
3. Passos de simulação executados (S1–S5) com comandos/manipulações exatas.
4. Resumo do build gate: build nativo, `dotnet build`, contagem de testes
   (passed/failed/skipped).
5. Defeitos abertos/feitos (ids dos JSON) com severidade.

## Critérios de Aceite

- [ ] **CA-5.1** — CA4 em máquina AMD real: export usa AMF encode +
      D3D11VA decode (evidência em log), fluxo FSR4 roda (ou downgrade
      justificado pela Story 01), sem regressão do fluxo FSR1 atual.
- [ ] **CA-5.2** — CA1: upscale FSR1 processa end-to-end com output correto
      (sem corrupção, resolução conforme config) — via simulação declarada
      (S3/S4), já que não há GPU não-AMD física.
- [ ] **CA-5.3** — CA5 simulado: export completa com encoder de uso geral
      (libx265; NVENC/QSV se o ambiente dispuser) e saída válida
      (reproduzível, codec/duração corretos) nos cenários S1/S2/S4.
- [ ] **CA-5.4** — CA3 simulado: fluxos FSR1/FSR3 completam com
      `amfrt64.dll` indisponível, usando apenas DLLs redistribuídas
      legítimas do bundle (S1–S5).
- [ ] **CA-5.5** — Todos os testes existentes + novos testes da cascata
      passam (`dotnet test CATRA.sln` verde — build gate).
- [ ] **CA-5.6** — Relatório em
      `.agents/sprint_atual/qa_report_story_05_amd_degradacao.md` declara,
      por critério (CA1–CA5), o que foi validado em hardware real vs.
      simulação, incluindo passos de simulação e pendência NVIDIA/Intel.

## Dependências

| Item | Por quê |
|------|---------|
| Subtask 01 (FSR4 fix) concluída | Passo 1 valida o fim do "fallback permanente"; sem ela CA-5.1 fica contaminado |
| Subtask 02 (cascata encoder) concluída | Cenários S1/S2/S4 testam a cascata AMF→FFmpeg→libx265 |
| Subtask 03 (FSR3 FG offline) concluída | Cenário S5 valida o resultado do spike (FG real ou fallback sinalizado) |
| Subtask 04 (eventos UI) concluída | Validação das mensagens de fallback com dedup; se pendente, essa parte fica BLOCKED no relatório (não derruba o resto) |
| `lib/amf`, vcpkg, `lib/onnxruntime` presentes | Build gate do Passo 0 |
| Mídia de teste (vídeo curto) | Todos os cenários E2E |

## Notas

- **Risco residual conhecido** (do recon, para o relatório): comportamento
  do AGS/FFX em GPU NVIDIA real e FL_12_0 hardcoded (`d3d_interop.cpp:308`)
  só são exercitáveis com hardware físico — ficam como pendência declarada.
- Se um defeito bloquear um cenário inteiro (ex.: cascata não dispara),
  registrar como `severity: high/critical` e continuar com os demais
  cenários — o relatório deve refletir o estado real, não parar no 1º erro.
- Sandbox é descartável: nunca commitar binários nem DLLs manipuladas.

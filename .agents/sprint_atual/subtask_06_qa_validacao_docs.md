# Subtask 06: QA final — validação E2E da integração FSR/FFX contra os critérios A1–A7 + documentação `docs/FSR_FFX_INTEGRATION.md`

**Story:** story_06_qa_validacao_docs.md
**Tipo:** qa
**Complexidade:** alta
**Agente:** qa-tester-alta

> Justificativa da complexidade (skill complexity-eval): escopo QA com E2E tests e
> fluxos multi-camadas (nativo C++/DX12 ↔ P/Invoke C# ↔ WPF/HwndHost), medições de
> performance com critérios mensuráveis (presents ≥1,5× frames decodificados, drift
> A/V ≤ ±40 ms), matriz combinatória upscale×FG, cenários de falha induzida
> (DLL ausente/corrompida, swapchain fail, resize sob FG) e validação de artefatos
> de export via ffprobe → enquadra em "E2E tests, multi-layer flows, performance".
> Não é `critical` (sem escopo security/zero-defect/chaos engineering).

## Descrição

Validação ponta a ponta da integração FidelityFX (FFX API 2.x — upscale FSR 4/3.1 +
Frame Generation playback) contra **todos** os critérios de aceite do plano aprovado
(`.agents/specs/fsr-sdk-integration_plan.md`, ajustes PO A1–A7), com execução da
matriz completa de cenários abaixo, registro de evidências (saídas de comando, logs,
medições) e produção de:

1. **Relatório de QA** (`.agents/sprint_atual/qa_report_06_validacao_fsr_ffx.md`)
   com resultado por cenário da matriz, medições A4/A5, hardware/driver usados e
   evidências — anexado ao reporte da sprint.
2. **Documentação final** `docs/FSR_FFX_INTEGRATION.md` (seções obrigatórias no
   passo 8).
3. **Script de verificação** `scripts/validate-fsr-ffx.ps1` (padrão
   `scripts/test-rife-backends.ps1`) para reproduzir as checagens automatizáveis.

**Regra dura da story:** NÃO alterar código de produção nesta subtask (`src/`,
`native/`). Defeito encontrado → registrar como `bugfix_NN_*.json` em
`.agents/sprint_atual/` (mesmo padrão de `bugfix_04_sw_decode_fallback.json` /
`bugfix_05_episode_status_refresh.json`), sem tocar nos já abertos, e marcar o
cenário como FAIL no relatório. Exceção de escrita: apenas testes/automação de QA
novos estritamente necessários (ex.: tool de medição) devem ser propostos antes no
relatório, não criados unilateralmente.

**Hardware:** cenários completos exigem GPU AMD (idealmente RDNA 4 para FSR 4 ML).
Em GPU não-RDNA4 registrar explicitamente o comportamento de fallback observado
(FSR 3.1 / indisponível); cenário não executável por hardware fica `SKIPPED(hw)`
com justificativa no relatório (nunca `PASS` sem execução).

## Arquivos Alvo (fileScope)

**Criar (3):**
- `.agents/sprint_atual/qa_report_06_validacao_fsr_ffx.md` — relatório da matriz
  (tabela cenário × resultado × evidência), medições A4/A5 brutas, ambiente
  (GPU/driver/versões), lista de defeitos abertos.
- `docs/FSR_FFX_INTEGRATION.md` — documentação final (passo 8).
- `scripts/validate-fsr-ffx.ps1` — script de verificação (passo 7).

**Criar condicional (0..N):**
- `.agents/sprint_atual/bugfix_06_*.json` (e seguintes) — um por defeito novo
  encontrado; mesmo schema dos `bugfix_*.json` existentes.

**Modificar:** nenhum arquivo de produção. Leituras de referência permitidas:
`native/catra-gpu/tools/` (smoke tests), `tests/` (suites existentes), `docs/`,
`lib/FidelityFX-SDK-2.3.0/` (read-only).

**Commits:** apenas arquivos do fileScope (**nunca `git add .`** — repo sujo fora
do escopo, risco 5 do plano).

## Passos

### 0. Gate inicial (skill build-gate)

1. `dotnet build CATRA.sln` — deve passar **sem nenhum SDK FSR em build-time**
   (tudo runtime-loaded; verificar também que o build nativo
   `pwsh ./scripts/build-native.ps1 -Configuration Release` passa).
2. `dotnet test` — baseline da suíte antes de executar a matriz.

### 1. Grupo B — Build-gate e testes unitários

| ID | Cenário | Verificação |
|----|---------|-------------|
| B1 | `dotnet build CATRA.sln` sem SDK FSR em build-time | exit code 0, 0 erros/warnings novos |
| B2 | Build nativo (`scripts/build-native.ps1`) sem `.lib` FFX linkada | exit code 0; `dumpbin` confirma sem import FFX em `catra-gpu.dll` |
| B3 | `dotnet test` completo (Core/Data/Services/UI) | 100% passam; nenhuma regressão vs baseline do passo 0 |
| B4 | Smoke nativo `catra-interop-test` (regressão do tool existente) | PASS |
| B5 | Smoke nativo `catra-fg-smoke-test` (subtask 04) com GPU/DLLs disponíveis | PASS (se indisponível: `--expect-unavailable` passa) |

### 2. Grupo F — Fallback (sem DLLs FFX / GPU incompatível)

| ID | Cenário | Verificação |
|----|---------|-------------|
| F1 | App inicia e reproduz vídeo **sem nenhuma DLL FFX** no diretório (renomear/mover as 8 DLLs) | playback normal no renderer D3D11 atual; upscale cai para FSR 1; FG fica indisponível na UI; 0 crashes; log registra o motivo |
| F2 | Sem DLLs: `catra_is_fsr4_available()==0` e `catra_is_fg_available()==0` | via smoke test / app; sem crash |
| F3 | GPU não-RDNA4 (se hardware disponível): pedir FSR 4 | `catra_is_fsr4_available()` conforme runtime (FSR 3.1 fallback); output correto; registrar comportamento real. Sem hardware adequado → `SKIPPED(hw)` |

### 3. Grupo P — Playback com DLLs + GPU AMD (A1, A4, A5, A6)

| ID | Cenário | Verificação |
|----|---------|-------------|
| P1 | **A1** — as 8 DLLs presentes na saída do app (`amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_framegeneration_dx12.dll`, `amd_ags_x64.dll`, `amd_acs_x64.dll`, `D3D12Core.dll`, `dxcompiler.dll`, `dxil.dll`) e carregando | `dir` + `validate-fsr-ffx.ps1` + smoke nativo de carga; todas load OK |
| P2 | Upscale method=2 (FSR4) produz output correto | smoke nativo e/ou QA visual (nitidez sem corrupção de cor/green frame) |
| P3–P8 | **Matriz playback** {upscale off, FSR1, FSR4} × {FG off, FG on} — 6 combinações | cada combo: renderiza sem crash, seleção de renderer correta (FG off → D3D11 atual; FG on → FsrFrameGenRenderer), saída visual íntegra |
| P9 | **A4** — clipe 30 fps + FG on: presents ≥ 1,5× frames decodificados | medição via observer do smoke FG e/ou contadores do app; esperado ≈2× em display ≥60 Hz; registrar razão medida |
| P10 | **A5** — drift A/V ≤ ±40 ms com FG on | procedimento passo 6; registrar valor medido e método |
| P11 | **A6** — default FSR1+FG validado; FSR4+FG permitido (limitação zero-MV) | combos P3–P8 cobrem; confirmar default da settings = FSR1+FG |
| P12 | Toggles de settings (upscale off/FSR1/FSR4; FG on/off) persistem entre sessões e aplicam em runtime | alterar → reiniciar app → conferir persistência e efeito |
| P13 | FG desabilitado → renderer D3D11 atual com comportamento inalterado (regressão) | reprodução idêntica ao estado pré-FG (sem novos warnings/erros em log) |

### 4. Grupo E — Export offline (A2)

| ID | Cenário | Verificação (ffprobe) |
|----|---------|----------------------|
| E1 | **A2** — export com upscale **FSR 1** (perfil DLNA) | `ffprobe -v error -show_streams -show_format <out>.mp4`: container mp4 válido, stream de vídeo presente com codec/dimensões esperadas (dimensão upscaled), duração ≈ fonte, stream de áudio presente; exit code 0 |
| E2 | Export com upscale **FSR 4** (contorno AMF: cópia D3D12→D3D11 na fronteira do encode) | mesmos critérios de E1 + QA visual do arquivo gerado (sem green/black frame) |
| E3 | Export baseline **sem upscale** (regressão) | mesmos critérios ffprobe |

### 5. Grupo X — Falhas induzidas (sem crash, UX degradada correta)

| ID | Cenário | Indução | Verificação |
|----|---------|---------|-------------|
| X1 | DLL loader ausente | remover `amd_fidelityfx_loader_dx12.dll` | F1/F2: fallback completo, 0 crash |
| X2 | DLL individual ausente (repetir p/ upscaler, framegeneration, ags, D3D12Core, dxcompiler, dxil) | remover uma por vez | `ProbeDependencyDlls` detecta; availability=0 no recurso afetado; fallback sem crash |
| X3 | DLL corrompida | truncar `amd_fidelityfx_upscaler_dx12.dll` (ex.: 50% dos bytes) | LoadLibrary falha → log + fallback FSR 1; 0 crash |
| X4 | **A4** — criação do FG swapchain falha | runtime FG indisponível (remover `amd_fidelityfx_framegeneration_dx12.dll`) com FG habilitado na settings | fallback automático para renderer D3D11, playback continua, 0 crash; log registra |
| X5 | Resize de janela durante FG ativo | redimensionar janela em playback FG | recreate de swapchain sem crash; playback continua no tamanho novo |
| X6 | Storm de resize (várias mudanças rápidas) durante FG | arrastar borda da janela continuamente | sem crash/deadlock (debounce do caller — Story 05); app recupera |

### 6. Procedimento de medição A4/A5 (obrigatório documentar o método real usado)

1. **A4 (razão de presents):** usar o observer de presents do smoke FG
   (contadores total/gerados) E/ou os contadores do app real (frames decodificados
   vs presents); condição: display ≥ 60 Hz (registrar taxa do display). Aprova com
   `presents ≥ 1,5 × frames_decodificados` para clipe 30 fps.
2. **A5 (drift A/V):** método preferencial — clipe de teste com marcador de sync
   (flash visual + beep no mesmo instante, gerável via ffmpeg); com FG on, comparar
   o instante do flash apresentado vs beep, frame a frame (tolerância ±40 ms).
   Método alternativo aceito — timestamps de log: relógio de áudio do PlaybackEngine
   vs timestamp de apresentação do mesmo frameID; registrar valores brutos (mínimo 3
   execuções, início/meio/fim do clipe) e o desvio máximo observado.
3. Registrar no relatório: clipe usado (fps/duração), GPU/driver, taxa do display,
   método, dados brutos e resultado.

### 7. `scripts/validate-fsr-ffx.ps1` (padrão `test-rife-backends.ps1`)

PowerShell com `$ErrorActionPreference = 'Stop'`, blocos numerados com status
colorido `✓/✗` e resumo final, automatizando o que não exige interação visual:
1. Presença das 8 DLLs (A1) no diretório de saída do app (e na saída do smoke nativo).
2. Execução de `catra-interop-test` e `catra-fg-smoke-test` (detectar
   `PASS`/skip gracioso; flag `-ExpectUnavailable` para modo sem DLLs).
3. Validação ffprobe de exports informados via parâmetro (`-ExportPath <mp4>...`):
   container, streams, codec, dimensões, duração (comparada com `-SourcePath` se
   fornecido); usar `lib/ffmpeg/ffprobe.exe`.
4. Checagem de atribuição MIT/copyright AMD (A7) junto aos headers vendorados e ao
   diretório de deploy das DLLs.
5. Exit code ≠ 0 se qualquer checagem obrigatória falhar (gate automatizável).

### 8. `docs/FSR_FFX_INTEGRATION.md` (seções obrigatórias)

Formato segue o padrão de `docs/RIFE_BACKENDS.md` (título, Overview, seções com
comandos em blocos de código). Conteúdo mínimo:
1. **Overview + Arquitetura:** loader runtime (`LoadLibrary`/`GetProcAddress` das 5
   funções `ffxFunctions`, sem `.lib`/dependência de build-time), módulos
   `ffx_runtime`, `upscale_fsr4`, `catra_fg_*`, contorno AMF (cópia D3D12→D3D11 na
   fronteira do encode — referência `docs/FSR_AMF_ISSUE_CONTEXT.md`), fluxo
   upscale → FG no playback, seleção de renderer no PlaybackEngine.
2. **Modo vídeo zero-MV:** upscale temporal com motion vectors zerados (RG32F),
   depth dummy, jitter (0,0), reset em scene-cut; **limitação** de ghosting em
   movimento/cortes; FSR 1 (EASU) permanece default de export.
3. **Frame Generation (playback-only):** requisitos (GPU AMD/DX12, DLLs, display),
   swapchain no HWND do HwndHost, optical flow interno (sem Prepare), FG **não** se
   aplica a export offline; procedimento de medição A4/A5 (passo 6).
4. **Deploy das DLLs:** lista completa das 8 DLLs (A1), origem, destino na saída,
   uso do `validate-fsr-ffx.ps1`.
5. **Licenciamento (A7):** headers FFX SDK 2.3.0 vendorados sob MIT + copyright AMD;
   localização da LICENSE/notas junto a headers e DLLs.
6. **Troubleshooting:** tabela sintoma → causa → ação (DLLs ausentes/corrompidas,
   GPU não-RDNA4, FG swapchain fail → fallback, drift A/V, ghosting, green/black
   frame no export, resize sob FG).

### 9. Fechamento (skill build-gate)

1. Re-rodar B1/B3/B4/B5 após qualquer ajuste de script/doc para garantir nada
   quebrado.
2. Preencher relatório com a matriz completa; cada FAIL vira `bugfix_NN_*.json`
   (não consertar inline).
3. Commit apenas do fileScope.

## Critérios de Aceite

- [ ] **Build-gate:** `dotnet build CATRA.sln` + build nativo passam **sem** SDK FSR
      em build-time; `dotnet test` completo passa sem regressões; `catra-interop-test`
      e `catra-fg-smoke-test` passam (ou skip gracioso registrado).
- [ ] **Fallback:** app sem as DLLs FFX funciona com fallback (upscale FSR 1, FG
      indisponível) sem crash (F1/F2 com evidência).
- [ ] **A1:** as 8 DLLs presentes na saída e carregando (evidência: script + smoke).
- [ ] **A2:** export FSR 1 (perfil DLNA) E export FSR 4 entregam `.mp4` válido
      atestado por saída de `ffprobe` colada no relatório (streams, codec, dimensões,
      duração, áudio).
- [ ] **A4:** clipe 30 fps + FG on apresenta ≥ 1,5× os frames decodificados
      (esperado ≈2×), com a razão medida registrada no relatório.
- [ ] **A5:** drift A/V ≤ ±40 ms com FG on, com método e dados brutos registrados.
- [ ] **A4 (fallback):** falha de criação do FG swapchain → fallback automático para
      o renderer D3D11 sem crash (X4 com evidência).
- [ ] **A6:** matriz {off, FSR1, FSR4} × {FG off, FG on} (P3–P8) validada, com
      default FSR1+FG confirmado.
- [ ] **Falhas induzidas:** X1–X6 executados sem crash e com UX degradada correta
      (cada um com evidência; não reproduzível deterministicamente → registrado
      método alternativo ou `SKIPPED` com justificativa).
- [ ] **A7:** atribuição MIT/copyright AMD verificada junto a headers e DLLs.
- [ ] Settings toggles persistem e aplicam (P12); FG off = comportamento inalterado
      (P13).
- [ ] `docs/FSR_FFX_INTEGRATION.md` criada cobrindo as 6 seções obrigatórias do
      passo 8 (arquitetura, zero-MV, FG playback, deploy, licenciamento, troubleshooting).
- [ ] `scripts/validate-fsr-ffx.ps1` roda com exit code 0 no ambiente aprovado.
- [ ] Relatório `qa_report_06_validacao_fsr_ffx.md` com matriz × evidências anexado
      ao reporte da sprint; todo cenário com status PASS/FAIL/SKIPPED(hw).
- [ ] Nenhum arquivo de produção (`src/`, `native/`) modificado nesta subtask;
      defeitos novos registrados como `bugfix_NN_*.json`.
- [ ] Commits contêm APENAS arquivos do fileScope (nunca `git add .`).

## Dependências

- **subtask_01 (Story 01) — BLOQUEANTE:** `ffx_runtime` (Load/IsAvailable/
  ProbeDependencyDlls/ProbeDx12Adapter), deploy das 8 DLLs A1 no build, include
  dirs FFX.
- **subtask_02 (Story 02) — BLOQUEANTE:** backend de upscale FSR 4/3.1 real
  (method=2, modo vídeo zero-MV) + `catra_is_fsr4_available()` runtime-load.
- **subtask_03 (Story 03) — BLOQUEANTE:** contorno AMF genérico na fronteira do
  encode (cópia D3D12→D3D11) — pré-requisito de E1/E2 (A2).
- **subtask_04 (Story 04) — BLOQUEANTE:** módulo `catra_fg_*` + smoke
  `catra-fg-smoke-test` (observer de presents usado nas medições A4).
- **subtask_05 (Story 05) — BLOQUEANTE:** integração C#/WPF (`FsrFrameGenRenderer`,
  seleção de renderer no PlaybackEngine, toggles de settings + indicação de
  capacidade na UI, fallback automático em falha do FG swapchain, debounce de
  resize) — pré-requisito de P3–P13, X4–X6.
- **Hardware:** GPU AMD (idealmente RDNA 4) + display ≥ 60 Hz para os cenários
  completos (P2, P9, E2); na ausência, cenários correspondentes viram `SKIPPED(hw)`
  registrado — nunca aprovados por presunção.
- **Ferramentas:** `lib/ffmpeg/ffprobe.exe` (validação de exports), PowerShell 7
  (scripts), ambiente de build existente (`scripts/build-native.ps1`).
- **Referências (read-only):** `.agents/specs/fsr-sdk-integration_plan.md` (A1–A7),
  `docs/FSR_AMF_ISSUE_CONTEXT.md`, `native/catra-gpu/tools/` (padrão de smoke
  nativo), `scripts/test-rife-backends.ps1` (padrão de script), `docs/RIFE_BACKENDS.md`
  (padrão de doc).

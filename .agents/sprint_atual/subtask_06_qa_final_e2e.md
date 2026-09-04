# Subtask 06 — QA Final: validação end-to-end contra CA1–CA8 (gate de entrega)

| Campo       | Valor                                                |
|-------------|------------------------------------------------------|
| **Story**   | Story 06 — QA: validação final end-to-end contra a spec |
| **Tipo**    | qa                                                   |
| **Complexidade** | alta (E2E multi-layer native C++/C#/WPF, 8 critérios de aceite, cenários de concorrência, dedup E2E, performance sanity, gate final de entrega) |
| **Agente**  | qa-tester-alto                                       |
| **Dependências** | TODAS as anteriores: Subtasks 01, 02, 03, 04 e 05 completas (ver seção Dependências) |

## Descrição

Gate final do plano `non-amd-fsr-processing`. Esta subtask **não implementa
nada** — executa, mede, compara contra a spec e consolida evidências. QA não
corrige código (nota do PO, Story 05/06); defeitos viram registro de backlog
com severidade e retornam à story dev correspondente.

Escopo de execução:

1. **Build gate completo** (skill `build-gate`): native + .NET, bloqueando
   qualquer avanço se falhar.
2. **Suite de testes completa** (CA8): 4 projetos de teste + smoke tests
   nativos.
3. **Execução do app** nos 4 fluxos de processamento — `fsr4`, `fsr1`,
   `fsr3fg`, `rife` — mais export em cenário não-AMD **simulado** com
   cascata de encoder (estratégia R5: não há máquina não-AMD disponível).
4. **Validação de CADA critério CA1–CA8** da spec com status e evidência
   reproduzível, admitindo apenas as exceções previstas na própria spec:
   - CA7: causa raiz externa documentada é aceite válido.
   - CA1/CA3/CA5: simulação declarada (R5) é aceite válido.
   - CA2: ADR aprovado pelo usuário + fallback notificado é aceite válido
     (Caminho B da Story 03).
5. **Verificação do dedup de mensagens de fallback** (CA6) em E2E real:
   máximo 1 mensagem por tipo por job, incluindo job com múltiplos fallbacks
   simultâneos e jobs concorrentes.
6. **Relatório final consolidado** com evidências e declaração explícita do
   que foi validado em hardware real vs. simulado.

## Arquivos Alvo (fileScope)

### Read-only (verificação e coleta de evidência)

**Pipeline / serviços (Stories 02, 03, 04):**
- `src/CATRA.Services/Processing/ProcessingPipeline.cs` — pontos de fallback
  (interp ~215-234, upscale ~862-882, encoder ~267), `SelectedEncoder` no
  resultado, rota `fsr3fg`.
- `src/CATRA.Services/Processing/EncoderFallbackFactory.cs` — cascata
  AMF → FFmpeg hw → libx265; gatilhos `CATRA_ERR_DEVICE` (-4) e
  `CATRA_ERR_NOT_IMPL` (-2).
- `src/CATRA.Services/Processing/FFmpegCliEncoder.cs`,
  `src/CATRA.Services/Processing/AmfBridgeEncoder.cs`.
- `src/CATRA.Core/Processing/FallbackType.cs`,
  `src/CATRA.Core/Processing/FallbackEvent.cs`,
  `src/CATRA.Core/Processing/EncoderFallbackEventArgs.cs`.

**UI de processamento (Story 04):**
- `src/CATRA.UI/ViewModels/ProcessingQueueViewModel.cs` — dedup
  `HashSet<(JobId, FallbackType)>`, cleanup de job removido.
- `src/CATRA.UI/Controls/ProcessJobCardControl.xaml` — exibição das
  mensagens de fallback.
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` — `Fsr4Available` real +
  warnings (Story 01).

**Native (Stories 01, 03):**
- `native/catra-gpu/ffx_runtime.cpp`, `native/catra-gpu/upscale_fsr4.cpp` —
  logs de probe FFX (diagnóstico Story 01).
- `native/catra-gpu/interp_rife.cpp` — warn `fsr3fg→RIFE` não-silencioso.
- `native/catra-gpu/catra_fg*.cpp` — FG (Caminho A) preservado playback.

**Documentação das stories anteriores (insumo do relatório):**
- `docs/fsr4-fallback-diagnosis.md` (ou equivalente da Story 01).
- `docs/fsr3-fg-offline-spike-report.md` (Story 03, ambos os caminhos).
- `.agents/decisions/ADR-*-fsr3-fg-offline.md` — **se Caminho B**: conferir
  se a aprovação do usuário está registrada (nota do PO, CA-6.1/CA2).
- Relatório da Subtask 05 (validação AMD + degradação simulada) — reusar
  evidências quando aplicável, revalidando o que o gate final exige.

**Testes automatizados:**
- `tests/CATRA.Services.Tests/Processing/EncoderFallbackFactoryTests.cs`
- `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs`
- `tests/CATRA.UI.Tests/ProcessingQueueViewModelTests.cs`

### Artefatos produzidos por esta subtask

- `.agents/sprint_atual/qa_relatorio_final_story06.md` — relatório final
  consolidado (estrutura no Passo 7).
- `docs/sprints/story06-e2e/` — evidências brutas: logs `[catra-gpu]`/
  `[FFX]`/`[ProcessingPipeline]`, saídas `ffprobe`, screenshots da UI com
  mensagens de fallback, resultados da suite de testes.

## Passos

### Passo 0 — Pré-condições (bloqueantes)

1. Confirmar: Subtasks 01–04 concluídas e Subtask 05 executada com relatório
   existente. Se alguma estiver aberta → subtask fica `BLOCKED`, reportar
   ao orquestrador.
2. Confirmar: se a Story 03 seguiu Caminho B, o ADR correspondente tem
   aprovação explícita do usuário registrada. Sem isso, CA2 não pode ser
   declarado verificado.
3. Restaurar o ambiente para estado limpo (todas as DLLs no lugar:
   `amfrt64.dll` do driver acessível, 8 DLLs FFX em
   `runtimes/win-x64/native/` ao lado de `catra-gpu.dll`).

### Passo 1 — Build gate (skill `build-gate`)

1. Build completo (native + .NET):
   ```powershell
   .\build_native_now.bat
   ```
   (alternativa: `.\scripts\build-native.ps1` + `dotnet build CATRA.sln --no-restore`)
2. Verificar deploy: `catra-gpu.dll` + as 8 DLLs FFX presentes em
   `$(OutDir)runtimes\win-x64\native\`; conferir se há cópia órfã de
   `catra-gpu.dll` na raiz do bin SEM as DLLs FFX (hipótese raiz da Story 01).
3. Se build falhar → status `BLOCKED`, registrar saída completa, NÃO
   prosseguir. Nenhuma evidência posterior é válida com build quebrado.

### Passo 2 — Suite completa de testes (CA8)

1. `dotnet test --no-build` — executar os 4 projetos:
   `CATRA.Core.Tests`, `CATRA.Data.Tests`, `CATRA.Services.Tests`,
   `CATRA.UI.Tests`. Registrar contagem passed/failed/skipped por projeto.
2. Verificar **presença E verde** dos testes novos exigidos pela spec:
   - Cascata de encoder (Story 02): gatilhos `-4`/`-2`, ordem da cascata,
     eventos disparados, códigos fora do gatilho NÃO fazendo fallback.
   - Eventos de fallback no pipeline (Story 04): emissão por tipo.
   - Dedup no ViewModel (Story 04): mínimo 3 cenários — mesmo tipo 2× no
     mesmo job → 1 mensagem; tipos diferentes → 2 mensagens; mesmo tipo em
     jobs diferentes → 1 por job.
   - Fallback `fsr3fg→RIFE` não-silencioso (Story 03).
3. Smoke tests nativos (se o build os produziu):
   `ffx_load_smoke_test.exe`, `fg_smoke_test.exe`,
   `interop_readback_test.exe` em `native/catra-gpu/build/Debug/`.
4. Qualquer teste falhando → CA8 `FAILED`; defeito aberto contra a story
   dona do teste.

### Passo 3 — Execução do app: cenários E2E na máquina dev (AMD RDNA4)

Rodar `dotnet run --project src/CATRA.App` (ou binário publicado).
**Matriz de arquivos de teste** (todos os cenários usam pelo menos os dois
primeiros):
- A: vídeo curto 1080p com áudio, 24 ou 30 fps.
- B: vídeo curto SEM trilha de áudio.
- C (opcional, se disponível): resolução diferente entre jobs (4K/720p) —
  exercita rebuild do interop pool.

**Cenários:**

| # | Cenário | Configuração | Verificação | CA coberto |
|---|---------|--------------|-------------|------------|
| E1 | `fsr4` + `rife` | fluxo completo | log `[ProcessingPipeline]`/`[FFX]` comprova FSR4 real **OU** causa raiz externa documentada (Story 01); output válido por `ffprobe` | CA7 |
| E2 | `fsr1` + `rife` | upscale FSR1 | job completo; `ffprobe` confere resolução alvo; sem corrupção visual (spot-check de frames); codec/duração corretos | CA1, CA4 |
| E3 | `fsr3fg` | frame generation | Caminho A: contagem de quadros do output > contagem fonte e coerente com fps alvo (`ffprobe`) **OU** Caminho B: fallback notificado na UI + ADR aprovado | CA2 |
| E4 | `rife` puro | baseline | contagem de quadros idêntica ao baseline pré-sprint (sem regressão); `catra_fg` de playback intacto | CA-3.6 (regressão) |
| E5 | Export não-AMD **simulado** | renomear/ocultar `amfrt64.dll` (ou `SetDllDirectory` equivalente documentado) | cascata AMF → FFmpeg → libx265 dispara; log `Encoder selected: libx265` (ou NVENC/QSV se o ambiente dispuser); job completo; saída HEVC válida (`ffprobe`: codec, duração, reprodução) | CA5 (simulado) |
| E6 | Sem FFX **simulado** | remover temporariamente as 8 DLLs FFX do lado de `catra-gpu.dll` | fluxos `fsr1`/`rife` completam sem componentes de driver; só DLLs redistribuídas do bundle em uso; fallback upscale (se `fsr4` pedido) notificado | CA3 (simulado) |
| E7 | Concorrência | 2+ jobs simultâneos, sendo ≥1 com fallback | dedup por job isolado (E7 comprova CA6 sob concorrência); sem GPU hang (0x887A0001), sem deadlock de keyed mutex, sem crash; ambos os jobs completam | CA6, robustez |
| E8 | Todos os fallbacks de uma vez | job único com `fsr4`+`fsr3fg` sob simulação E5+E6 combinada | exatamente 1 mensagem por tipo (`Upscale`, `Encoder`, `FrameGen`/`Interp`) — nunca duplicada; formato "X indisponível → usando Y" | CA6 |

**Regras de execução:**
- Coletar para cada cenário: log completo do app (canais `[catra-gpu]`,
  `[FFX]`, `[ProcessingPipeline]`), saída `ffprobe` do arquivo de saída,
  screenshot da UI quando houver mensagem de fallback. Salvar em
  `docs/sprints/story06-e2e/E{n}/`.
- **Restaurar o ambiente após cada simulação** (voltar `amfrt64.dll` e as 8
  DLLs FFX) antes do próximo cenário. Registrar no relatório os passos de
  simulação exatos executados (exigência R5).
- Se um cenário E1–E4 falhar por defeito de código → registrar defeito,
  marcar o CA correspondente como `FAILED` e continuar os demais cenários
  quando independente (maximizar evidência do relatório).

### Passo 4 — Verificação dedicada do dedup de fallback (CA6)

Além de E7/E8, verificar explicitamente:

1. **Repetição dentro do mesmo job:** forçar condição que emita o mesmo tipo
   de fallback mais de uma vez (ex.: retry interno) → UI mantém 1 mensagem.
2. **Jobs distintos:** mesmo tipo de fallback em 2 jobs paralelos → 1
   mensagem em cada card (isolamento por jobId).
3. **Cleanup:** após conclusão do job, card removido sem vazamento de
   mensagens para jobs seguintes (confirmar `RefreshActiveJobs` limpando o
   HashSet de dedup).
4. **Job sem fallback:** nenhuma mensagem exibida (UI limpa).
5. Revalidar os testes unitários de dedup do Passo 2 como evidência
   automatizada complementar.

### Passo 5 — Validação CA1–CA8 (tabela de status)

Preencher a tabela abaixo com status por critério. Status admitidos:
`PASSED` (real), `PASSED_SIMULATED` (aceite via R5, obrigatório declarar),
`PARTIAL`, `FAILED`, `BLOCKED`. Toda linha exige evidência apontando para
`docs/sprints/story06-e2e/`, saída de teste ou documento da story.

| CA | Critério (resumo) | Evidência exigida | Exceção admitida pela spec |
|----|-------------------|-------------------|----------------------------|
| CA1 | FSR1 upscale E2E em GPU não-AMD com output correto | E2 + simulação E5/E6: `ffprobe` resolução + spot-check visual | Simulação declarada (R5) |
| CA2 | `fsr3fg` aplica FG real OU ADR aprovado + fallback notificado | E3: contagem de quadros (Caminho A) ou ADR aprovado + mensagem na UI (Caminho B) | ADR com aprovação do usuário |
| CA3 | Fluxos sem componentes de driver (Adrenalin/`amfrt64.dll`) | E5 + E6 com passos de simulação registrados | Simulação declarada (R5) |
| CA4 | Máquina AMD: AMF encode + D3D11VA decode, sem regressão | E1/E2 em estado limpo: log `Encoder selected: AMF` + decode D3D11VA | — (hardware real disponível) |
| CA5 | Não-AMD: export completa com encoder de uso geral | E5: `libx265`/NVENC/QSV selecionado + saída HEVC válida | Simulação declarada (R5) |
| CA6 | UI exibe fallbacks; máx 1 mensagem por tipo por job | E7/E8 + Passo 4 (screenshots + testes de dedup) | — |
| CA7 | FSR4 executa no ecossistema dev OU causa raiz externa documentada | E1: log de probe FFX + diagnóstico Story 01 | Causa raiz externa documentada |
| CA8 | Testes existentes + novos (cascata, eventos) passam | Passo 2: saída `dotnet test` completa | — |

### Passo 6 — Triagem de defeitos

1. Nenhum defeito blocker/alto pode ficar aberto no fechamento do gate:
   qualquer blocker/alto → gate `REPROVADO`, defeito roteado para a story
   dev dona do componente (01/02/03/04) via orquestrador.
2. Defeitos menores remanescentes → registrar como backlog no schema JSON de
   `.agents/project/testing-conventions.md` (severidade `medium`/`low`,
   `complexidade`, passos, evidência). Não bloqueiam o gate, mas DEVEM
   constar no relatório.
3. QA **não corrige código** nesta subtask.

### Passo 7 — Relatório final consolidado

Escrever `.agents/sprint_atual/qa_relatorio_final_story06.md` com, no
mínimo, estas seções:

1. **Resultado por CA** — tabela do Passo 5 preenchida (status + evidência +
   link/caminho da evidência).
2. **Ambientes usados** — máquina dev (AMD Radeon RX 9070 XT, RDNA 4,
   ecossistema FSR4), builds (native + .NET), versões relevantes.
3. **Validações reais vs. simuladas (R5)** — declaração explícita, por CA,
   do que foi executado em hardware real e do que foi simulação, com os
   passos de simulação exatos (DLLs ocultadas/removidas, caminhos forçados).
4. **Limitações conhecidas** — ex.: causa externa de CA7 (se aplicável),
   custo de FG offline (U6 do spike), overhead do readback no fallback.
5. **Pendências** — obrigatório: validação em hardware NVIDIA/Intel real
   para CA1/CA3/CA5 (pendente de máquina, não bloqueia per R5); quaisquer
   outras pendências herdadas das stories.
6. **Defeitos** — blockers/altos (se houve) e backlog de menores com
   severidade.
7. **Veredito do gate** — `APROVADO` / `APROVADO_COM_RESSALVAS` /
   `REPROVADO`, com justificativa amarrada à tabela de CAs.

## Critérios de Aceite

- [ ] **CA-6.1** — Cada critério CA1–CA8 da spec tem status verificado com
      evidência reproduzível — admitindo as exceções previstas na própria
      spec: CA7 por causa raiz externa documentada; CA1/CA3/CA5 por
      simulação declarada (R5); CA2 por ADR aprovado pelo usuário +
      fallback notificado.
- [ ] **CA-6.2** — Cenários E2E exercidos na máquina dev: fluxo `fsr4` (com
      ecossistema FSR4), fluxo `fsr1`, fluxo `fsr3fg` (FG real OU ADR
      aprovado + fallback notificado), baseline `rife` e export em cenário
      não-AMD simulado com cascata de encoder.
- [ ] **CA-6.3** — CA6 verificado em E2E: tela de processamento exibe os
      fallbacks ativos com no máximo uma mensagem por tipo por job, incluindo
      job com múltiplos fallbacks simultâneos (E8) e jobs concorrentes (E7).
- [ ] **CA-6.4** — Todos os testes automatizados passam (CA8), incluindo os
      novos testes de cascata de encoder, eventos de fallback e dedup.
- [ ] **CA-6.5** — Nenhum defeito blocker/alto aberto; defeitos menores
      remanescentes registrados como backlog com severidade.
- [ ] **CA-6.6** — Relatório final existe em
      `.agents/sprint_atual/qa_relatorio_final_story06.md` contendo:
      resultado por CA, ambientes usados, validações reais vs. simuladas
      (R5), limitações conhecidas, pendências (ex.: validação em hardware
      NVIDIA/Intel real) e veredito do gate.

## Dependências

- **Subtask 01** (Story 01 — fix FSR4 fallback permanente + availability na
  UI): diagnóstico e fix/decisão de causa externa concluídos — alimenta E1/CA7.
- **Subtask 02** (Story 02 — cascata de encoder): implementação + testes —
  alimenta E5/CA5.
- **Subtask 03** (Story 03 — FSR3 FG offline): relatório do spike + Caminho
  A implementado OU Caminho B com ADR aprovado pelo usuário — alimenta
  E3/CA2.
- **Subtask 04** (Story 04 — eventos de fallback + dedup UI): implementação
  + testes de dedup — alimenta E7/E8/CA6.
- **Subtask 05** (Story 05 — validação AMD + degradação simulada): relatório
  de validação prévia — insumo direto; evidências podem ser reutilizadas mas
  o que o gate final exige deve ser revalidado.
- **Se Caminho B adotado na Story 03:** aprovação explícita do usuário no
  ADR é pré-condição para declarar CA2 verificado.
- Subtask 06 é o último item do sprint: só inicia quando todas acima
  estiverem `completed`.

## Notas

- **QA não corrige código** (nota do PO). Defeito encontrado → registro com
  severidade e retorno à story dev correspondente via orquestrador.
- **Build gate é inegociável** (skill `build-gate`): sem build + testes
  verdes, nenhum cenário E2E é considerado e o gate é `REPROVADO`/`BLOCKED`.
- A estratégia R5 da spec é o contrato para CA1/CA3/CA5: simulação é válida,
  mas **deve ser declarada** — o relatório sem a declaração real-vs-simulado
  não satisfaz CA-6.6.
- Restaurar o ambiente (DLLs) após cada simulação; um cenário de simulação
  esquecido contamina os cenários seguintes e invalida evidências.
- Se a execução revelar profundidade ilimitada (ex.: defeito em cascata que
  exija re-teste iterativo de todas as stories), emitir
  `ESCALA_NECESSARIA: <descrição>` ao orquestrador em vez de expandir escopo
  unilateralmente.
- Classificação de severidade e schema de defeitos:
  `.agents/project/testing-conventions.md`.

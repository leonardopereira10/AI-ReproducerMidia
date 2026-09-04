# Subtask 01 — Fix FSR4 fallback permanente + availability real na UI

| Campo       | Valor                                                |
|-------------|------------------------------------------------------|
| **Story**   | Story 01 — Corrigir "FSR4 sempre em fallback" + disponibilidade real na UI |
| **Tipo**    | dev                                                  |
| **Complexidade** | alta                                             |
| **Agente**  | developer-alta                                       |
| **Dependências** | nenhuma (read-only recon já concluído)          |

## Descrição

Na máquina dev (RDNA4 + ecossistema FSR4 instalado), o app **sempre** executa
o fluxo de fallback FSR1 mesmo com FSR4 disponível. Adicionalmente,
`SettingsViewModel.Fsr4Available` nunca recebe o valor real da probe nativa —
permanece hardcoded `true` —, tornando o warning de indisponibilidade inútil.

### Hipótese principal (baseada em recon técnico)

`catra-gpu.dll` é carregado pelo runtime .NET via resolução standard de
P/Invoke (`[DllImport("catra-gpu.dll")]`), que procura em:
1. `AppContext.BaseDirectory` (raiz do bin, ex.: `bin/Debug/net8.0-windows/`)
2. `AppContext.BaseDirectory/runtimes/win-x64/native/`

O **build** (`CATRA.App.csproj` target `BuildNativeBridge`) copia
`catra-gpu.dll` **e as 8 DLLs FFX** para
`$(OutDir)runtimes\win-x64\native\` — correto. Porém, se algo fizer
`catra-gpu.dll` resolver da **raiz** do bin (ex.: cópia manual, artefato de
build anterior, ou interferência de `SetDllDirectory(lib/ffmpeg)` no
`App.xaml.cs:395`), o `FfxRuntime::ModuleDirWide()` resolve o diretório do
módulo para a raiz — onde as 8 DLLs FFX **não** existem. O probe falha em
`ProbeDependencyDlls()` e `Fsr4IsAvailable()` retorna false.

`SetDllDirectory(lib/ffmpeg)` é chamado durante `ConfigureFfmpeg()` e
restaurado com `SetDllDirectory(null)` no finally — janela curta, mas pode
interferir se a resolução do P/Invoke do `catra-gpu.dll` ocorrer **durante**
essa janela (improvável mas possível em timing de startup).

### Problema UI

`SettingsViewModel` (construtor, linha ~60-90) carrega settings do repo mas
**nunca consulta** `INativeBridge.IsFsr4Available()`. O campo `_fsr4Available`
permanece `true` (default). Ninguém injeta. O warning `RefreshUpscaleWarning()`
existe (linha 690-695) mas nunca dispara.

## Arquivos Alvo (fileScope)

### Diagnóstico (read + instrumentação de log)
- `native/catra-gpu/ffx_runtime.cpp` — `Load()`, `ProbeDependencyDlls()`,
  `IsAvailable()`: adicionar log com o path resolvido por `ModuleDirWide()`
  e resultado de cada DLL probe (linha ~219-238).
- `native/catra-gpu/upscale_fsr4.cpp` — `Fsr4IsAvailable()` (linha 210-255):
  log explícito de qual etapa falhou (Load / ProbeDependencyDlls / context probe).
- `native/catra-gpu/catra_gpu.cpp` — `catra_is_fsr4_available()` (linha 622-633):
  log do resultado final.

### Fix de resolução / Deploy das DLLs
- `src/CATRA.App/CATRA.App.csproj` — target `BuildNativeBridge`: verificar
  se há cenário onde `catra-gpu.dll` vai para a raiz sem as DLLs FFX; se
  necessário, adicionar cópia **também** para a raiz com as 8 DLLs FFX, OU
  garantir que nenhuma cópia para raiz exista.
- `src/CATRA.App/App.xaml.cs` — `ConfigureFfmpeg()`: confirmar que
  `SetDllDirectory` não interfere; se interferir, mover a init do bridge
  para depois do `SetDllDirectory(null)`.

### Injeção de availability real na UI
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` — construtor (linha ~35-90):
  receber `INativeBridge` via DI, consultar `IsFsr4Available()` **após**
  bridge init, setar `_fsr4Available` com o valor real.
  **Nota**: a bridge exige `Initialize(d3d11Device)` antes de
  `IsFsr4Available()` funcionar (precisa de `g_device` válido). No startup
  da tela de Settings, não há device D3D11VA ativo. **Alternativa**: expor
  um método de probe que não dependa de init (carregar FFX + checar DLLs +
  adapter DX12 transiente — mesmo fluxo do `IsAvailable()` do `ffx_runtime`),
  ou chamar após primeiro `catra_init` e cachear. Avaliar no passo 2.
- `src/CATRA.App/App.xaml.cs` — registro DI de `SettingsViewModel`:
  adicionar `INativeBridge` como parâmetro do construtor (já registrado
  como Transient).
- `src/CATRA.Core/Interfaces/INativeBridge.cs` — já declara
  `IsFsr4Available()` (linha 49). Confirmar que funciona sem init ou
  documentar requisito.

### Testes
- `tests/CATRA.Services.Tests/Processing/NativeBridgeTests.cs` — testes de
  `IsFsr4Available()` com mock (já existem padrões).
- `tests/CATRA.UI.Tests/` — teste de `SettingsViewModel`: verificar que
  `Fsr4Available` é setado via ctor quando bridge mock retorna false.

## Passos

### Passo 1 — Diagnóstico com evidência (log)

1. Adicionar logs detalhados em `ffx_runtime.cpp`:
   - `ModuleDirWide()`: logar o path completo resolvido.
   - `ProbeDependencyDlls()`: logar cada DLL (presente/ausente) com path.
   - `Load()`: logar fullPath do loader DLL.
2. Adicionar logs em `upscale_fsr4.cpp::Fsr4IsAvailable()`:
   - Log de entrada e de cada gate (Load → ProbeDLLs → context probe).
3. Build + run na máquina dev. Capturar stdout/stderr (canais `[catra-gpu]`
   e `[FFX]`).
4. Analisar: qual etapa falha?
   - Se `ModuleDirWide()` aponta para raiz do bin → causa = deploy errado.
   - Se aponta para `runtimes/win-x64/native/` mas DLLs ausentes → causa =
     build não copia DLLs.
   - Se Load + ProbeDLLs OK mas context probe falha → causa = runtime
     FFX/AGS incompatível (documentar como externa per CA7).
5. Documentar evidência na story (ou em `docs/fsr4-fallback-diagnosis.md`).

### Passo 2 — Fix de resolução / Deploy das DLLs

Com base no diagnóstico:

**Cenário A (mais provável): DLLs ausentes na raiz do bin.**
- Verificar se existe cópia de `catra-gpu.dll` na raiz do output dir
  (`$(OutDir)` sem o `runtimes/win-x64/native/`). Se sim, remover.
- Se `NativeLibraryLoader.DetectAvailability()` encontra primeiro na raiz
  (linha ~107, `Path.Combine(baseDir, LibraryName)`), o runtime .NET carrega
  dessa localização — e `ModuleDirWide()` resolve para a raiz.
- **Fix**: remover qualquer item de projeto que copie `catra-gpu.dll` para
  a raiz, OU copiar as 8 DLLs FFX também para a raiz (menos limpo).

**Cenário B: SetDllDirectory interfere.**
- Mover `ConfigureFfmpeg()` para depois da primeira init do bridge, ou
- Reordenar startup para que bridge init + `SetDllDirectory` não overlap.

**Cenário C: Causa externa (runtime FFX/AGS).**
- Documentar em `docs/` e na story. Marcar CA-1.3 como satisfeito via
  documentação (per CA7 da spec).

### Passo 3 — Injeção de availability real na UI

1. Modificar `SettingsViewModel`:
   - Adicionar parâmetro `INativeBridge bridge` no construtor.
   - Após `_loading = false`, consultar `bridge.IsFsr4Available()` e setar
     `_fsr4Available`.
   - **Problema**: bridge não inicializada (sem D3D11 device) em Settings.
     Avaliar:
     - Opção A: `NativeBridge.IsFsr4Available()` já trata (retorna false se
       library unavailable, linha 380-387). Mas `catra_is_fsr4_available()`
       native depende de `g_device` (null antes de init) → retorna 0.
     - Opção B: criar método de probe leve que não exige init — carregar
       FFX, checar DLLs, criar device DX12 transiente (mesmo fluxo do
       `FfxRuntime::IsAvailable()`). Expor via novo P/Invoke
       `catra_is_ffx_available()` (sem necessidade de `catra_init`).
     - Opção C: aceitar que a probe só funciona após primeiro playback;
       injetar disponibilidade "desconhecida" até lá.
   - **Recomendação**: Opção B — probe leve independente de init.
2. Mesmo padrão para FG availability (se `catra_is_fg_available()` existe).
3. Warning `RefreshUpscaleWarning()` já está correto — dispara quando
   `UpscaleMethod == "fsr4" && !Fsr4Available`.

### Passo 4 — Testes

1. `NativeBridgeTests`: mock `INativeLibrary` com `IsFsr4Available()` → 0;
   verificar `NativeBridge.IsFsr4Available()` == false.
2. `SettingsViewModel` tests (novo ou existente):
   - Construir VM com bridge mock `IsFsr4Available() == false`;
     verificar `Fsr4Available == false` e `UpscaleWarning` não vazio.
   - Construir VM com bridge mock `IsFsr4Available() == true`;
     verificar `Fsr4Available == true` e `UpscaleWarning` vazio.
3. Teste manual na máquina dev:
   - Build completo; processar com `fsr4`; verificar log `[catra-gpu]`
     mostra "upscale_fsr4: available" (ou evidência de causa externa).
   - Abrir Settings; verificar warning ausente (FSR4 disponível) ou
     presente (sem FSR4).

## Critérios de Aceite

- [ ] **CA-1.1** — Diagnóstico registrado com evidência (log) apontando
      onde a probe FFX falha.
- [ ] **CA-1.2** — Causa raiz documentada na story ou em `docs/`.
- [ ] **CA-1.3** — Na máquina dev com FSR4, fluxo `fsr4` executa FSR4 real
      (log comprova) OU causa raiz documentada como externa (CA7 da spec).
- [ ] **CA-1.4** — `Fsr4Available` na UI reflete probe real, não default
      hardcoded.
- [ ] **CA-1.5** — Warning "FSR 4 não disponível" exibido quando probe
      retorna indisponível; ausente quando disponível.
- [ ] **CA-1.6** — Fallback FSR4→FSR1 continua funcionando quando FSR4 é
      genuinamente indisponível (sem regressão).
- [ ] **CA-1.7** — Testes existentes passam (build gate).

## Notas

- `NativeBridge.IsFsr4Available()` (linha 380-387) já é segura: retorna
  false quando library unavailable. O problema é que o **native** depende
  de `g_device` (ponteiro do D3D11 device do decoder), que é null até
  `catra_init()`.
- A spec (CA7) aceita causa raiz externa como resolução — não forçar fix
  se o FFX runtime/AGS for o bloqueio.
- `SetDllDirectory` tem janela curta (configure → init → restore) mas deve
  ser investigada como interferência potencial.

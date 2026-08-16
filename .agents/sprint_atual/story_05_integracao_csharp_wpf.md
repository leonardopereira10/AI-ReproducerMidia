# Story 05 — Integração C#/WPF: renderer FG, seleção no PlaybackEngine e settings UI

**Sprint:** SPRINT_04 · **Spec/Plano:** `.agents/specs/fsr-sdk-integration_plan.md` (aprovado 2026-08-13)
**Tipo:** dev

## Descrição

Consumo em C# dos módulos nativos das Stories 02/04, fechando a experiência de usuário:

1. **Novo `IVideoRenderer` — `FsrFrameGenRenderer`**: usado quando FG está habilitado;
   assume o HWND do HwndHost e submete frames decodificados via `catra_fg_present`.
   **Submissão dirigida pelo clock de playback (não pelo present)** — mitigação do risco
   A/V sync (Risco 6/A5).
2. **Seleção de renderer no `PlaybackEngine`**: factory de `IVideoRenderer` — FG on →
   `FsrFrameGenRenderer`; FG off ou falha de criação do swapchain FG → **fallback
   automático para o renderer D3D11 atual, sem crash** (A4).
3. **P/Invokes**: extensão do `NativeBridge.cs` (ou bridge de playback dedicada) com a
   ABI `catra_fg_*` (A3) e interface correspondente.
4. **Settings**: toggles de upscale (**off/FSR1/FSR4**) e **FG on/off** com persistência
   e aplicação; **indicação de capacidade na UI** (ex.: "FG indisponível neste sistema")
   baseada em `catra_is_fg_available()`/`catra_is_fsr4_available()`.
5. **Matriz upscale×FG no playback (A6)**: uso simultâneo **permitido** — upscale é
   aplicado ao frame antes da submissão ao FG backbuffer (upscale → FG).
   Default: **FSR1 (EASU) + FG**; FSR4 + FG permitido (limitação zero-MV documentada).
6. **Testes unitários C#**: seleção de renderer pelo factory (incl. caminho de fallback),
   wrappers NativeBridge, persistência/aplicação de settings.

## FileScope (preliminar)

**Criar:**
- `src/CATRA.Services/Playback/FsrFrameGenRenderer.cs` (novo `IVideoRenderer` FG)

**Modificar:**
- `src/CATRA.Services/Playback/PlaybackEngine.cs` — factory/seleção de renderer + fallback automático (A4)
- `src/CATRA.Services/Processing/NativeBridge.cs` + `src/CATRA.Core/Interfaces/INativeBridge.cs` — P/Invokes `catra_fg_*`, `catra_is_fg_available`, `catra_is_fsr4_available` (ou bridge de playback dedicada, se o projeto indicar)
- `src/CATRA.Core/Models/AppSettingsModel.cs` — novos campos: upscale mode (off/FSR1/FSR4), FG on/off
- `src/CATRA.Data/Repositories/AppSettingsRepository.cs` — persistência dos novos campos
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` — VMs dos toggles + capability display
- `src/CATRA.UI/Views/SettingsView.xaml` (+ `.xaml.cs` se necessário) — toggles + indicação de capacidade
- `src/CATRA.App/CATRA.App.csproj` — somente se necessário DI/wiring adicional

**Testes (criar/atualizar):** projeto(s) de teste existentes — renderer selection/factory,
NativeBridge wrappers, settings.

**NÃO tocar:** `VideoRenderer.cs` (renderer D3D11 atual vira o caminho de fallback e não
deve mudar de comportamento), pipeline de export/encode, pipeline RIFE.

## Critérios de Aceite

- [ ] Playback com FG habilitado: clipe 30 fps com FG on apresenta **≥1,5×** a contagem
      de frames decodificados (presents ~2×) **e** drift A/V **≤ ±40 ms** (A4/A5).
- [ ] FG desabilitado → renderer D3D11 atual, **comportamento inalterado**.
- [ ] Falha na criação do FG swapchain → **fallback automático para renderer D3D11, sem crash** (A4).
- [ ] Settings: toggles de upscale (off/FSR1/FSR4) e FG on/off **persistem e são aplicados**;
      UI mostra indicação de capacidade quando o recurso não está disponível.
- [ ] Matriz A6: upscale+FG simultâneos funcionam (upscale → FG); default FSR1+FG;
      FSR4+FG funciona com a limitação zero-MV documentada.
- [ ] Sem DLLs FFX em runtime: app funciona com fallback (upscale FSR 1, FG indisponível)
      sem crash; UI reflete indisponibilidade.
- [ ] Testes unitários novos passam (`dotnet test`); **sem regressões nos existentes**.

## Dependências

- **Story 01** (DLLs/probes), **Story 02** (upscale FSR 4 para a matriz A6),
  **Story 04** (ABI `catra_fg_*`).

## Notas

- Medição de presents/drift para A4/A5: contador nativo ou timestamp de submissão vs.
  relógio de áudio (`Clock.cs`); detalhar método no reporte da story para o QA reproduzir.
- Risco 2 (composição WPF HwndHost) e Risco 6 (A/V sync) do plano são mitigados aqui.

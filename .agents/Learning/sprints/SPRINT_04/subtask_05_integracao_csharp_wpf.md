# Subtask 05: Integração C#/WPF — FsrFrameGenRenderer (IVideoRenderer), seleção no PlaybackEngine com fallback, P/Invokes e settings de playback (upscale + FG)

**Story:** story_05_integracao_csharp_wpf.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alta

> Justificativa da complexidade (skill complexity-eval): fluxo multi-camadas
> C# ↔ P/Invoke ↔ ABI nativa ↔ GPU (FG swapchain + upscale DX12), factory de
> renderer com fallback automático em falha, concorrência (re-init de bridge por
> device, storm de resize, decode loop), persistência de settings + UI de
> capacidade, e critério de performance mensurável (presents ≥1,5× / drift ≤ ±40 ms)
> → enquadra em "fluxos complexos + multi-layer + performance" = **alta**.

## Descrição

Consumo em C# da ABI `catra_fg_*` da subtask_04 fechando a experiência de playback
com Frame Generation: novo `IVideoRenderer` (`FsrFrameGenRenderer`), seleção de
renderer no factory do `PlaybackEngine` com **fallback automático para o
`VideoRenderer` D3D11 sem crash** (A4), P/Invokes no `NativeBridge`, toggles de
settings **upscale playback (off/FSR1/FSR4)** + **FG on/off** com persistência e
indicação de capacidade na UI, e a matriz A6 (upscale → FG, default **FSR1 + FG**).
Inclui a instrumentação de **contagem de presents e drift A/V** para o QA (A4/A5).

### Fatos verificados no código (base obrigatória)

1. **Frames de playback hoje são SEMPRE software BGRA** (`byte[]`): o
   `VideoDecoder.cs` (playback) faz `av_hwframe_transfer_data` + `sws_scale` para
   BGRA em `CreateFrame` (linha ~364: "Read the GPU picture back on FFmpeg's own
   device ... Future optimisation: wrap the renderer's device ..."). O caminho
   `VideoFrame.CreateHardware` existe mas o decoder de playback não o produz.
   ⇒ O `FsrFrameGenRenderer` precisa apenas de upload BGRA → textura D3D11;
   frame hardware recebido = edge case documentado (ver passo 5).
2. **Factory do renderer já existe**: `PlaybackEngine` recebe
   `Func<IVideoRenderer> _videoRendererFactory` e cria o renderer em
   `EnsureRendererInitialized()`; `SetOutputWindow` descarta/recria o renderer
   quando o HWND muda; `ResizeOutput` chama `IVideoRenderer.Resize`. DI em
   `App.xaml.cs`: `services.AddTransient<IVideoRenderer, VideoRenderer>()` +
   factory `() => sp.GetRequiredService<IVideoRenderer>()` no singleton
   `IPlaybackEngine`.
3. **Pacing dirigido pelo clock já existe** (mitigação Risco 6/A5): o decode loop
   espera `WaitUntilDueAsync(video.PresentationTime)` contra o `Clock` (audio
   master) antes de `Present`. A submissão ao FG continua dirigida pelo clock; o
   módulo nativo só bloqueia no `Present(1,0)` (contrato subtask_04).
4. **Padrão P/Invoke** (`NativeBridge.cs`): `INativeLibrary` interno (seam de
   teste via `InternalsVisibleTo("CATRA.Services.Tests")`) → `NativeLibraryLoader`
   com `[DllImport("catra-gpu.dll", CallingConvention.Cdecl)]` privados +
   `DetectAvailability()` por arquivo; `NativeBridge` mapeia código negativo →
   `NativeBridgeException`, degrada graciosamente sem DLL (queries → defaults,
   destroy → no-op), e é **device-aware** (`Initialize(device)` faz re-init quando
   o device muda — `catra_shutdown` destrói todos os contextos nativos).
5. **Upscale nativo produz `ID3D12Resource*` NÃO compartilhável**:
   `catra_upscale_process` (FSR1/FSR4) retorna recurso criado com
   `D3D12_HEAP_FLAG_NONE` (`upscale_fsr4.cpp:392`, idem FSR1) — só o passthrough
   (method=0) retorna `ID3D11Texture2D*`. `catra_fg_present` exige
   `ID3D11Texture2D*` BGRA (contrato subtask_04). O helper interno
   `interop_share_d3d12_to_d3d11` existe mas **não é exportado na C ABI** e exige
   heap compartilhável ⇒ **não há caminho na ABI atual para a matriz A6
   (upscale → FG)** — resolvido na DECISÃO D2 abaixo (2 exports nativos aditivos).
6. **Settings**: chave/valor SQLite (`IAppSettingsRepository.Set/Get/GetAll`),
   modelo tipado `AppSettingsModel.Load` com defaults + constantes de chave,
   seed em `DatabaseInitializer.DefaultSettings` (INSERT OR IGNORE), auto-save
   por propriedade em `SettingsViewModel` (`partial void OnXChanged` + flag
   `_loading`), capabilities hoje hardcoded (`_fsr4Available = true`).
7. **UI**: `SettingsView.xaml` usa `DockPanel Style=Row` + `ComboBox`/`CheckBox`
   com binding direto; labels via `ProcessMethodToLabelConverter`
   (`fsr4→"FSR 4"`, `fsr1→"FSR 1"` — sem entrada para `"off"` ainda).
8. `catra_is_fg_available()` nativo retorna 1 **somente com `catra_init` já
   executado** (contrato subtask_04) ⇒ o probe de capacidade precisa inicializar o
   bridge num device próprio antes de consultar (DECISÃO D5).

### Decisões de design (documentar no código e em `docs/FSR_FFX_INTEGRATION.md` — a doc é da Story 06; aqui apenas os comentários)

- **D1 — Frame path**: frames BGRA de software são copiados (upload
  `UpdateSubresource`) para uma textura D3D11 default-heap owned pelo renderer
  (cache por geometria), então submetidos via `catra_fg_present` (zero-copy read,
  ownership do caller). Sem upscale, essa textura é o frame; com upscale, é a
  saída share-back do passo D2.
- **D2 — ⚠ DECISÃO DE ESCOPO (review gate): 2 exports nativos ADITIVOS.** A
  matriz A6 é impraticável com a ABI assumida (fato 5). FileScope inclui adições
  mínimas e aditivas em `native/catra-gpu` (nenhuma assinatura existente muda):
  1. `catra_d3d12_to_d3d11(void* d3d12_tex, void** out_d3d11_tex)` — GPU-GPU copy
     do recurso DX12 (não-shareable) para textura shared DX12 do interop +
     `interop_share_d3d12_to_d3d11` → `ID3D11Texture2D*` no device do bridge;
     bloqueia até a cópia completar (fence) para o caller poder liberar o DX12
     imediatamente após o retorno.
  2. `catra_fg_stats(int ctx, uint64_t* out_total_presents, uint64_t* out_generated)`
     — contadores mantidos no `FgRenderer::Impl` (trampoline de present já existe
     por contrato da subtask_04) para o QA medir a razão de presents no app real.
  Sem esses exports, o fallback seria: A6 = upscale off obrigatório com FG (viola
  a story) ou medição externa via PresentMon (aceitável apenas para a contagem).
  **Se o review vetar toque em nativo, ESCALAR antes de implementar A6.**
- **D3 — Seleção + fallback**: novo `IVideoRendererFactory` (Core) com
  implementação `VideoRendererFactory` (Services) que lê settings + capacidade a
  cada `Create()` (renderer nasce por sessão/HWND ⇒ toggle vale na próxima sessão
  de playback — comportamento documentado). O `PlaybackEngine` ganha um 2º factory
  opcional (fallback, sempre `VideoRenderer`) e tenta o fallback **quando
  `Initialize` do renderer primário lançar** (dispose do falho + create do
  fallback + Initialize; se o fallback também falhar, propaga para o caminho de
  `Error` existente). Falha de present no meio da sessão (device removed) segue o
  caminho de `Error` existente (sem crash) — recrear renderer com o vídeo em andamento
  está fora de escopo (risco residual).
- **D4 — Settings**: chaves novas `playback_upscale_mode`
  (`"off"|"fsr1"|"fsr4"`, default **`"fsr1"`**) e `frame_generation_enabled`
  (`"true"|"false"`, default **`"true"`**) — matriz A6 default FSR1+FG. Não
  reutilizar `upscale_method` (é do pipeline offline). **Upscale de playback só é
  aplicado no caminho FG** (upscale → FG backbuffer); com FG off o `VideoRenderer`
  permanece com **comportamento inalterado** (critério de aceite explícito) e a UI
  exibe nota informativa.
- **D5 — Capacidade**: novo `IGpuCapabilityService` (singleton): cria um device
  D3D11 próprio (lazy), `INativeBridge.Initialize(device)` (habilita
  `catra_is_fg_available`), consulta e cacheia `IsFgAvailable`/`IsFsr4Available`.
  Criação de device falhou (sem GPU) → ambos `false` + log. O `SettingsViewModel`
  passa a consumir o serviço (substitui o `_fsr4Available` hardcoded).
- **D6 — Resize com debounce**: `catra_fg_resize` é recreate completo (caro).
  `FsrFrameGenRenderer.Resize` apenas registra o tamanho pendente; a aplicação
  (coalescida) ocorre no próximo `Present`. Sem timers, sem storm.
- **D7 — Concorrência bridge**: o `NativeBridge` é singleton device-aware; um job
  de processamento concorrente faz `Initialize(decoderDevice)` → `catra_shutdown`
  → contexto FG morre (`CATRA_ERR_CONTEXT`/`dead`). Mitigação: no `Present`, erro
  nativo recuperável (`CATRA_ERR_CONTEXT`/`CATRA_ERR_INIT`) dispara **uma**
  tentativa de re-init (`bridge.Initialize(deviceDoRenderer)` + `catra_fg_create`);
  persistindo → lança (engine `Error`, sem crash). Documentado como limitação:
  processar fila enquanto assiste FG pode interromper a sessão FG.
- **D8 — Medição A4/A5 (QA)**: o engine acumula por sessão: frames lidos,
  presents submetidos, drift `clock.Current − frame.PTS` no instante do present
  (max/avg ms) — exposto em `IPlaybackEngine.GetSessionStats()` e logado no fim da
  sessão; o `FsrFrameGenRenderer` lê `catra_fg_stats` no dispose/fim e loga
  total/gerados/submetidos + razão. Protocolo de medição completo no passo 11.

## Arquivos Alvo (fileScope)

**Criar (9):**
- `src/CATRA.Core/Enums/PlaybackUpscaleMode.cs` — enum `Off | Fsr1 | Fsr4` +
  helpers `Parse(string)`/`ToKey()` (`"off"|"fsr1"|"fsr4"`).
- `src/CATRA.Core/Models/PlaybackStats.cs` — readonly struct:
  `long FramesDecoded, FramesPresented; double DriftMaxMs, DriftAvgMs; string RendererName`.
- `src/CATRA.Core/Interfaces/IVideoRendererFactory.cs` —
  `IVideoRenderer Create(); IVideoRenderer CreateFallback();`
- `src/CATRA.Core/Interfaces/IGpuCapabilityService.cs` —
  `bool IsFgAvailable { get; } bool IsFsr4Available { get; } void Refresh();`
- `src/CATRA.Services/Playback/GpuCapabilityService.cs` — implementação D5.
- `src/CATRA.Services/Playback/FsrFrameGenRenderer.cs` — novo `IVideoRenderer` FG.
- `src/CATRA.Services/Playback/VideoRendererFactory.cs` — implementação D3.
- `tests/CATRA.Services.Tests/Playback/VideoRendererFactoryTests.cs`
- `tests/CATRA.Services.Tests/Playback/PlaybackEngineRendererFallbackTests.cs`
  (fakes novos — ex.: `FakeThrowingRenderer` — vão em `Fakes.cs` do projeto)

**Modificar (13):**
- `src/CATRA.Core/Interfaces/INativeBridge.cs` — membros novos:
  `bool IsFgAvailable(); IntPtr FgCreate(IntPtr hwnd, int w, int h, double fps);
  void FgPresent(IntPtr ctx, IntPtr texture, int w, int h); void FgResize(IntPtr ctx, int w, int h);
  void FgDestroy(IntPtr ctx); IntPtr ShareD3D12ToD3D11(IntPtr d3d12Tex);
  (ulong Total, ulong Generated) FgStats(IntPtr ctx);` (docstrings com ownership/erros).
- `src/CATRA.Services/Processing/NativeBridge.cs` — mesmos membros em
  `INativeLibrary`/`NativeLibraryLoader` (P/Invokes Cdecl privados) + wrappers em
  `NativeBridge` (padrão: `EnsureAvailable`+`ThrowIfError`; destroy/stats →
  best-effort/no-op quando indisponível).
- `src/CATRA.Services/Playback/PlaybackEngine.cs` — ctor ganha
  `Func<IVideoRenderer>? fallbackRendererFactory = null` (opcional; preserva
  call-sites); `EnsureRendererInitialized` com try/catch-fallback (D3); contadores
  de sessão + `GetSessionStats()` + log de resumo em Stop/MediaEnded/Error (D8);
  reset dos contadores em `OpenAsync`.
- `src/CATRA.Core/Interfaces/IPlaybackEngine.cs` — `PlaybackStats GetSessionStats();` (aditivo).
- `src/CATRA.Core/Models/AppSettingsModel.cs` — constantes
  `PlaybackUpscaleModeKey = "playback_upscale_mode"`,
  `FrameGenerationEnabledKey = "frame_generation_enabled"` + propriedades tipadas
  `PlaybackUpscaleMode PlaybackUpscale { get; set; } = PlaybackUpscaleMode.Fsr1;`
  `bool FrameGenerationEnabled { get; set; } = true;` + leitura em `Load`
  (fallback p/ default quando ausente/inválido).
- `src/CATRA.Data/Database/DatabaseInitializer.cs` — seed
  `("playback_upscale_mode", "fsr1")`, `("frame_generation_enabled", "true")`.
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` — seção Player:
  `PlaybackUpscaleMode PlaybackUpscale` (default `Fsr1`),
  `bool FrameGenerationEnabled` (default `true`), `bool FgAvailable` (+handler),
  `string FgWarning`, `string PlaybackUpscaleNote`; ctor ganha parâmetro OPCIONAL
  `IGpuCapabilityService? capability = null` (mantém testes atuais compilando);
  handlers `OnPlaybackUpscaleChanged`/`OnFrameGenerationEnabledChanged` persistem
  (padrão `_settings.Set` + `_loading`); `RefreshFgMessages()` (warnings/notas D4/D5).
- `src/CATRA.UI/Views/SettingsView.xaml` — na seção "Player": ComboBox
  "Upscale (playback)" (`PlaybackUpscaleOptions` + SelectedItem), CheckBox
  "Frame Generation" (`FrameGenerationEnabled`), `TextBlock` de capacidade
  (`FgWarning`) e nota informativa (`PlaybackUpscaleNote`), mesmo estilo das
  linhas existentes.
- `src/CATRA.UI/Converters/ProcessMethodToLabelConverter.cs` — entrada
  `"off" → "Desligado"` (aditivo).
- `src/CATRA.App/App.xaml.cs` — DI (passo 9).
- **⚠ nativo (D2, aditivo):** `native/catra-gpu/catra_gpu.h` + `catra_gpu.cpp` —
  declaração + entry points `catra_d3d12_to_d3d11` e `catra_fg_stats`
  (`GuardCabi`, lookup no registry `g_fgContexts`); `native/catra-gpu/catra_fg.h`
  + `catra_fg.cpp` — contadores `std::atomic<uint64_t>` (total/gerados) no
  `FgRenderer::Impl` incrementados no trampoline de present + getter.
- `tests/CATRA.Services.Tests/Processing/NativeBridgeTests.cs` — wrappers FG novos
  (extensão do `FakeNativeLibrary` existente).
- `tests/CATRA.UI.Tests/SettingsViewModelTests.cs` — toggles novos + capacidade.

**Testes novos adicionais:**
- `tests/CATRA.Core.Tests/AppSettingsModelTests.cs` — `Load` com as chaves novas
  (presente / ausente → default / inválida → default).
- `tests/CATRA.Data.Tests/RepositoryTests.cs` (ou arquivo novo no mesmo projeto) —
  seeding das 2 chaves novas pelo `DatabaseInitializer` (INSERT OR IGNORE: default
  só quando ausente).
- `tests/CATRA.Services.Tests/Playback/PlaybackEngineTests.cs` — casos de
  `GetSessionStats()` (contagens/drift/reset).

**NÃO tocar:** `VideoRenderer.cs` (caminho de fallback — comportamento inalterado),
`VideoDecoder.cs`/`AudioDecoder.cs`/`AudioRenderer.cs`, pipeline de export/encode
(`ProcessingPipeline.cs`), pipeline RIFE, `PlayerViewModel.cs`/`PlayerView.*`
(nenhuma mudança necessária — HWND/resize já fluem), files das subtasks 01/02
(exceto o aditivo D2 em `catra_gpu.*`/`catra_fg.*`). Commits apenas deste
fileScope (**nunca `git add .`** — repo sujo fora do escopo).

## Passos

### 1. Core — enums, models e interfaces

- `PlaybackUpscaleMode` + `Parse`/`ToKey` (strings persistidas `"off"/"fsr1"/"fsr4"`;
  desconhecido → `Fsr1` — o default A6).
- `PlaybackStats` (struct do passo D8; `RendererName` vem de
  `_videoRenderer.GetType().Name`).
- `IVideoRendererFactory`: `Create()` (decisão settings+capacidade) e
  `CreateFallback()` (sempre o renderer D3D11 base).
- `IGpuCapabilityService` (D5).
- `IPlaybackEngine.GetSessionStats()` (aditivo; docstring: valores da sessão
  atual/última sessão, reset em `OpenAsync`).
- `INativeBridge`: 7 membros novos (assinaturas no fileScope). Docstrings:
  `FgCreate` lança `NativeBridgeException` em código negativo (o renderer traduz
  para fallback); `FgDestroy`/`FgStats` best-effort (sem throw quando
  indisponível); `ShareD3D12ToD3D11` retorna textura owned-by-caller (liberar com
  `ReleaseTexture`), bloqueia até a cópia GPU terminar.

### 2. `NativeBridge.cs` — P/Invokes + wrappers

P/Invokes novos (privados, Cdecl, mesmo padrão dos existentes):

```csharp
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern int catra_is_fg_available();
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern int catra_fg_create(IntPtr hwnd, int w, int h, double videoFps, out int context);
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern int catra_fg_present(int context, IntPtr frameTexture, int frameW, int frameH);
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern int catra_fg_resize(int context, int w, int h);
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern void catra_fg_destroy(int context);
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern int catra_d3d12_to_d3d11(IntPtr d3d12Tex, out IntPtr d3d11Tex);
[DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
private static extern int catra_fg_stats(int context, out ulong totalPresents, out ulong generated);
```

Wrappers `NativeBridge` (espelhar convenções): `IsFgAvailable()` → `false` se
`!_library.IsAvailable`, senão `> 0`; `FgCreate` → `EnsureAvailable` +
`ThrowIfError` + `return (IntPtr)context`; `FgPresent`/`FgResize`/`ShareD3D12ToD3D11`
→ `EnsureAvailable` + `ThrowIfError`; `FgDestroy` → no-op quando indisponível;
`FgStats` → `(0,0)` quando indisponível ou erro (best-effort). Espelhar tudo em
`INativeLibrary` (seam de teste).

### 3. ⚠ Nativo aditivo (D2 — review gate)

- `catra_fg.h/.cpp`: 2 contadores atômicos no `Impl` incrementados no trampoline
  de present (total; gerados quando `isGeneratedFrame`), getter
  `GetStats(uint64_t*, uint64_t*)`; zerados no `Create`.
- `catra_gpu.h/.cpp`: `catra_fg_stats` (lookup em `g_fgContexts` + `GuardCabi`) e
  `catra_d3d12_to_d3d11` (`GuardCabi`; exige `g_initialized`: copia o recurso no
  device/queue D3D12 do interop para uma textura DX12 shared (pool simples por
  geometria/formato, `D3D12_HEAP_FLAG_SHARED`), espera fence, então
  `interop_share_d3d12_to_d3d11(shared, g_device, out)`; `CATRA_ERR_INIT`/
  `CATRA_ERR_INVALID_ARG`/`CATRA_ERR_DEVICE` nos caminhos de falha; `*out = null`
  em todo erro). Docstrings completas (ownership, threading, erros) no padrão do
  header. Nada mais muda no nativo.

### 4. `GpuCapabilityService` (D5)

- Lazy: `Vortice.Direct3D11.D3D11.D3D11CreateDevice` (DriverType.Hardware,
  feature level 11.0, sem swapchain); falhou → flags `false` + log (sem throw).
- Sucesso: `_bridge.Initialize(device.NativePointer)` → `IsFgAvailable =
  _bridge.IsFgAvailable()`, `IsFsr4Available = _bridge.IsFsr4Available()`;
  cacheia; `Refresh()` re-consulta (sem recriar device). Device mantido vivo no
  serviço (singleton; `Dispose` libera device após `_bridge.Shutdown()` NÃO é
  necessário — o bridge é compartilhado; apenas dispose do device).
- Threading: lock simples (probe uma vez).

### 5. `FsrFrameGenRenderer` (IVideoRenderer)

Ctor: `FsrFrameGenRenderer(INativeBridge bridge, PlaybackUpscaleMode upscaleMode)`
(testável com bridge fake). Estado: `_gate` (mesmo padrão do `VideoRenderer`),
device D3D11 próprio (Vortice, FL 11.0), contexto, `_fgCtx`, `_upscalerCtx`,
textura de upload cacheada, `_width/_height`, `_pendingResize`, flag `_dead`.

- `Initialize(hwnd,w,h)`: cria device → `_bridge.Initialize(devicePtr)` → valida
  `_bridge.IsFgAvailable()` (falso → lança `InvalidOperationException` — o factory
  já filtrou, mas o engine usa o throw para o fallback) →
  `_bridge.FgCreate(hwnd, w, h, fps)` — **fps do vídeo**: recebido por parâmetro
  opcional do factory (metadata `Fps` quando disponível; senão 0 → nativo trata
  como informativa) → `IsInitialized = true`. Qualquer falha → dispose parcial +
  rethrow (gatilho do fallback A4).
- `Present(frame)`: (a) aplicar `_pendingResize` se houver (D6: `FgResize`; falha
  → marca `_dead` + lança); (b) frame hardware (`IsHardwareFrame`) → lançar
  `NotSupportedException` com log claro (decoder de playback nunca produz — fato 1;
  zero-copy futuro é otimização fora de escopo); (c) upload BGRA:
  `EnsureUploadTexture(frame.Width, frame.Height)` (default heap BGRA, recriada em
  mudança de geometria) + `UpdateSubresource` (via contexto Vortice; usar
  `Marshal`/`fixed` no `byte[]`); (d) upscale (se `upscaleMode != Off`): dst =
  tamanho do **rect letterbox** do frame dentro de `_width×_height` (mesma conta
  `ComputeLetterboxRect` do `VideoRenderer` — duplicar o helper estático aqui;
  `VideoRenderer.cs` não pode mudar); dst igual ao frame → pula upscale; senão
  `EnsureUpscaler(srcW,srcH,dstW,dstH)` (recria em mudança de geometria/método;
  `CreateUpscaler(..., method: Fsr1=1, Fsr4=2)`) → `ProcessUpscale(tex)` (retorna
  DX12) → `_bridge.ShareD3D12ToD3D11(dx12)` → textura final; liberar o DX12 com
  `ReleaseTexture` logo após o share-back (cópia já completou — contrato D2);
  (e) `FgPresent(_fgCtx, texFinal, frame.Width/Height ou dstW/dstH conforme o
  caso)` — erro recuperável (`NativeBridgeException` com código
  `CATRA_ERR_CONTEXT`/`CATRA_ERR_INIT`): **uma** tentativa de recuperação
  `bridge.Initialize(device) + FgCreate` (D7); outro erro → `_dead = true` +
  lança; (f) liberar textura share-back com `ReleaseTexture` APÓS o present
  (zero-copy read exige validade durante o present); (g) contador
  `SubmittedPresents++`.
- `Resize(w,h)`: valida >0; registra `_pendingResize` (coalesce; aplicação no
  próximo Present — D6).
- `Clear()`: upload de textura preta + mesmo caminho de present (sem upscale).
- `Dispose()`: `FgDestroy` + `DestroyUpscaler` + `ReleaseTexture` pendentes +
  dispose device/contexto (idempotente, sob `_gate`). NÃO chama
  `_bridge.Shutdown()` (bridge compartilhado).
- Log: `DiagnosticsLogger` em init/resize/fallback-recovery/erro (mesmo estilo do
  `VideoRenderer`).

### 6. `VideoRendererFactory` (D3)

Ctor: `(IAppSettingsRepository settings, IGpuCapabilityService capability,
Func<FsrFrameGenRenderer> fgFactory, Func<IVideoRenderer> baseFactory)`.
- `Create()`: `AppSettingsModel.Load(_settings)` → se `FrameGenerationEnabled &&
  capability.IsFgAvailable` → `fgFactory()` (injetando o `PlaybackUpscale` lido),
  senão `baseFactory()`; log da decisão (motivo: off / indisponível / FG).
  Settings lidas A CADA `Create()` (renderer nasce por sessão — toggle aplica na
  próxima sessão; documentado).
- `CreateFallback()` → `baseFactory()` sempre.

### 7. `PlaybackEngine` — fallback (A4) + stats (D8)

- Ctor: parâmetro novo opcional `Func<IVideoRenderer>? fallbackRendererFactory`
  (após `clock`/`positionReportInterval` — presina call-sites e testes atuais).
- `EnsureRendererInitialized()`: fluxo atual; ao chamar `Initialize`, envolver em
  try/catch quando houver fallback factory: catch → log warn com exceção +
  `_videoRenderer.Dispose()` + `_videoRenderer = _fallbackRendererFactory()` +
  `Initialize` do fallback (falhou → rethrow; o loop morre pelo caminho de `Error`
  existente, sem crash). Registrar `RendererName` na stats.
- Decode loop: antes do `Present`, `long decoded = ++_framesDecoded` (na leitura
  do frame); após `Present` bem-sucedido: `_framesPresented++`; drift =
  `_clock.Current - video.PresentationTime` → acumular soma e máximo (ms) sob o
  mesmo lock/`Interlocked` existente. Reset em `OpenAsync` e no construtor.
- `GetSessionStats()` → snapshot (`lock _gate`).
- Log de resumo em `FinishStop`/`HandleMediaEnded`/`HandleError`:
  `[PlaybackEngine] session: renderer={RendererName} decoded={N} presented={M} driftMax={…}ms driftAvg={…}ms`.
- `FsrFrameGenRenderer`: no `Dispose` (chamado pelo engine em Stop/Dispose), antes
  do `FgDestroy` → `FgStats` e log:
  `[FsrFrameGenRenderer] presents total={T} generated={G} submitted={S} ratio={T/S:0.00}`.

### 8. Settings + UI (D4/D5)

- `AppSettingsModel` + `DatabaseInitializer`: conforme fileScope (defaults
  `fsr1`/`true` — A6).
- `SettingsViewModel`: hydrate no ctor (`_playbackUpscale = model.PlaybackUpscale`,
  `_frameGenerationEnabled = model.FrameGenerationEnabled`); capability:
  `Fsr4Available = capability?.IsFsr4Available ?? true;`
  `FgAvailable = capability?.IsFgAvailable ?? true;` (padrão headless true, como o
  `_fsr4Available` atual). Handlers: `OnPlaybackUpscaleChanged` →
  `_settings.Set(PlaybackUpscaleModeKey, value.ToKey())` + `RefreshFgMessages()`;
  `OnFrameGenerationEnabledChanged` → `Set(FrameGenerationEnabledKey, ToBoolString)`
  + `RefreshFgMessages()`; `OnFgAvailableChanged` → `RefreshFgMessages()`.
  `PlaybackUpscaleOptions` = `[Off, Fsr1, Fsr4]`. `RefreshFgMessages()`:
  - `FgWarning` = `FrameGenerationEnabled && !FgAvailable` →
    `"Frame Generation indisponível neste sistema (runtime FFX/GPU). O player usará o renderer padrão."` senão `""`.
  - `PlaybackUpscaleNote` = `PlaybackUpscale != Off && !FrameGenerationEnabled` →
    `"Upscale de playback só é aplicado com Frame Generation ativa."` senão `""`.
  - (manter `RefreshUpscaleWarning` existente do upscale offline intacto).
- `SettingsView.xaml` (seção Player, após o combo de Tema):
  ComboBox "Upscale (playback):" (`ItemsSource=PlaybackUpscaleOptions`,
  `SelectedItem=PlaybackUpscale`, template com `ProcessMethodToLabelConverter` —
  agora com `"off"→"Desligado"`); CheckBox "Frame Generation"
  (`IsChecked=FrameGenerationEnabled`); `TextBlock ErrorLabel` → `FgWarning`;
  `TextBlock` discreto → `PlaybackUpscaleNote`.
- Conversor: entrada `"off"` aditiva em `ProcessMethodToLabelConverter`.

### 9. DI — `App.xaml.cs`

```csharp
services.AddSingleton<IGpuCapabilityService, GpuCapabilityService>();
services.AddTransient<FsrFrameGenRenderer>(sp => new FsrFrameGenRenderer(
    sp.GetRequiredService<INativeBridge>(), PlaybackUpscaleMode.Fsr1)); // modo real injetado pelo factory
services.AddTransient<IVideoRendererFactory>(sp => new VideoRendererFactory(
    sp.GetRequiredService<IAppSettingsRepository>(),
    sp.GetRequiredService<IGpuCapabilityService>(),
    () => /* FsrFrameGenRenderer com upscale lido dentro do factory */,
    () => new VideoRenderer()));
services.AddSingleton<IPlaybackEngine>(sp => new PlaybackEngine(
    () => sp.GetRequiredService<IVideoDecoder>(),
    () => sp.GetRequiredService<IAudioDecoder>(),
    () => sp.GetRequiredService<IVideoRendererFactory>().Create(),
    () => sp.GetRequiredService<IAudioRenderer>(),
    fallbackRendererFactory: () => sp.GetRequiredService<IVideoRendererFactory>().CreateFallback()));
```

(Ajuste fino do wiring — ex.: factory construir o `FsrFrameGenRenderer`
diretamente com o modo lido — é liberdade de implementação; o contrato é:
`Create()` decide por settings+capacidade a cada chamada e `CreateFallback()`
sempre retorna o renderer D3D11 base. Remover/ignorar o registro
`AddTransient<IVideoRenderer, VideoRenderer>` se ficar órfão.)

### 10. Testes unitários novos (obrigatórios)

Padrões do projeto (xUnit + FluentAssertions, fakes em `Fakes.cs`; nada de
GPU/nativo real):

1. **`VideoRendererFactoryTests`**: (a) FG on + capacidade ok → retorna o renderer
   FG (fake/distinção por tipo) com o modo de upscale lido; (b) FG on +
   `IsFgAvailable=false` → renderer base; (c) FG off → renderer base; (d) settings
   relidas a cada `Create()` (mudou no repo → próxima chamada reflete);
   (e) `CreateFallback()` sempre base.
2. **`PlaybackEngineRendererFallbackTests`**: (a) primário lança no `Initialize` →
   fallback criado, `Play` apresenta frames sem erro (assert `PresentCount > 0` no
   fake fallback, `State=Playing`, nenhum evento `Error`); (b) primário ok →
   fallback nunca criado; (c) primário E fallback falham → evento `Error` dispara
   (sem crash); (d) sem fallback factory (null) → comportamento legado (exceção
   propagada no caminho de `Error`).
3. **`PlaybackEngineTests` (stats)**: sessão com fakes → `GetSessionStats()`
   reporta `FramesPresented` == frames apresentados, drift ≥ 0 e preenchido;
   reset após novo `OpenAsync`; `RendererName` preenchido.
4. **`NativeBridgeTests` (extensão)**: `FakeNativeLibrary` com os 7 membros novos:
   `IsFgAvailable` indisponível → `false`; `FgCreate` código negativo →
   `NativeBridgeException`; `FgPresent`/`FgResize`/`ShareD3D12ToD3D11` mapeiam
   erro; `FgDestroy`/`FgStats` sem DLL → no-op/`(0,0)` sem throw; handle
   int↔IntPtr preservado.
5. **`SettingsViewModelTests` (extensão)**: defaults `Fsr1`/`true`; persistência
   das 2 chaves no repo fake ao mudar (e não durante o hydrate — `_loading`);
   valores salvos recarregados em novo VM; capability fake `IsFgAvailable=false` →
   `FgWarning` preenchido; `IsFsr4Available=false` + `fsr4` → warning existente;
   upscale ≠ Off com FG off → `PlaybackUpscaleNote` preenchido.
6. **`AppSettingsModelTests` (novo, Core.Tests)**: `Load` com chaves presentes
   (valores válidos), ausentes (defaults `Fsr1`/`true`) e inválidas
   (`"xxx"`/`"notabool"` → defaults).
7. **Data.Tests**: `DatabaseInitializer` seed cria `playback_upscale_mode="fsr1"`
   e `frame_generation_enabled="true"`; segunda execução não sobrescreve valor
   modificado pelo usuário (INSERT OR IGNORE).

### 11. Medição/reporte para o QA (A4/A5) + build-gate

**Como medir (contrato com o QA):**
- **Drift A/V (A5)**: campo `driftMax`/`driftAvg` da linha de log de sessão do
  engine (`[PlaybackEngine] session: ...`). drift = `clock − PTS` no instante da
  submissão; áudio é o master. Aceite: `driftMax ≤ 40 ms`.
- **Razão de presents (A4)**: linha `[FsrFrameGenRenderer] presents total=…
  generated=… submitted=… ratio=…` (contadores nativos da subtask_04 via
  `catra_fg_stats`). Aceite: `ratio = total/submitted ≥ 1,5` (esperado ≈ 2,0 em
  display 60 Hz com conteúdo 30 fps). Correlacionar com `decoded`/`presented` da
  linha do engine (baseline: FG off → ratio ausente/1,0).
- **Protocolo**: clipe 30 fps conhecido, sessão ≥ 30 s, janela em tamanho fixo;
  rodar 2× (FG on / FG off) e colar as linhas de log no reporte. Caminho do log:
  `DiagnosticsLogger.LogFilePath`. Validação externa opcional: PresentMon na janela
  do player.

**Build-gate (obrigatório antes de completar):**
1. `pwsh ./scripts/build-native.ps1 -Configuration Release` (pelos 2 exports D2) +
   `dumpbin /exports …/catra-gpu.dll | findstr "catra_d3d12_to_d3d11 catra_fg_stats"`.
2. `dotnet build CATRA.sln` verde.
3. `dotnet test` verde (novos + regressão zero).
4. Smoke manual (se GPU/DLLs disponíveis): playback com FG on/off + resize; sem
   DLLs FFX: app abre, settings mostram indisponibilidade, playback segue no
   renderer base sem crash.

## Critérios de Aceite

- [ ] `FsrFrameGenRenderer` implementa `IVideoRenderer` consumindo a ABI
      `catra_fg_*` assumida (create/present/resize/destroy) via `NativeBridge`;
      submissão dirigida pelo clock do engine (sem pacing próprio).
- [ ] Factory do `PlaybackEngine` seleciona FG quando `frame_generation_enabled`
      **e** `catra_is_fg_available()`; FG off ou indisponível → `VideoRenderer`
      (comportamento inalterado).
- [ ] Falha no `Initialize` do FG (ex.: create do swapchain) → **fallback
      automático para o `VideoRenderer`, sem crash**, com log (A4).
- [ ] Settings: `playback_upscale_mode` (off/fsr1/fsr4, default fsr1) e
      `frame_generation_enabled` (default true) persistem, recarregam e são
      aplicados na criação do renderer; UI mostra indicação de capacidade
      (`FgWarning`) quando FG indisponível.
- [ ] Matriz A6: upscale → FG implementado (upscale no frame antes do
      `catra_fg_present`); FSR4+FG sujeito ao downgrade nativo para FSR1 quando
      FSR4 indisponível (UI avisa).
- [ ] Medição A4/A5 disponível: stats de sessão no engine (drift max/avg, contagens)
      + contadores nativos de presents logados; protocolo de reprodução documentado.
- [ ] Sem DLLs FFX em runtime: nenhum crash — fábrica cai no renderer base,
      `IsFgAvailable=false`, UI reflete indisponibilidade.
- [ ] Testes unitários novos (factory, fallback, stats, wrappers NativeBridge,
      settings, modelo/seeding) passam; `dotnet test` sem regressões.
- [ ] `dotnet build CATRA.sln` + build nativo passam; os 2 exports aditivos
      verificados no `dumpbin` (se D2 aprovado no review).
- [ ] Commits contêm APENAS o fileScope (nunca `git add .`).

## Dependências

- **subtask_04 (Story 04) — BLOQUEANTE**: ABI `catra_fg_*`
  (`catra_is_fg_available`, `catra_fg_create(hwnd,w,h,fps,out_ctx)`,
  `catra_fg_present(ctx,tex,w,h)`, `catra_fg_resize`, `catra_fg_destroy`),
  registry `g_fgContexts`, trampoline de present (base dos contadores D2) e o
  smoke nativo validando o módulo. Se a implementação final divergir das
  assinaturas assumidas, adaptar os P/Invokes (contrato de erros CATRA_ERR_* é o
  mesmo do `catra_gpu.h`).
- **subtask_03 (Story 03) — SERIALIZAÇÃO**: `catra_gpu.h/.cpp` são modificados lá
  (contorno AMF); os 2 exports aditivos D2 tocam os MESMOS arquivos — merge apenas
  com a subtask_03 já integrada (rebase; conflitos previstos apenas nas listas de
  entry points).
- **subtask_02 (Story 02)**: backend de upscale FSR4/FSR1 runtime-loaded consumido
  no caminho A6 (`CreateUpscaler`/`ProcessUpscale` existentes no bridge — sem
  mudança).
- **subtask_01 (Story 01)**: deploy das DLLs FFX (disponibilidade do FG em
  runtime) e `ffx_runtime` — indireta, via `catra_is_fg_available`.
- Infraestrutura existente (NÃO modificar): `NativeBridge`/`INativeLibrary` (padrão
  P/Invoke), `PlaybackEngine` factories + `Clock` (audio master), `AppSettingsModel`
  /`AppSettingsRepository`/`DatabaseInitializer`, `VideoHostControl` (HWND).
- Consumidor futuro (não é dependência): Story 06 (QA — reproduzir protocolo do
  passo 11; `docs/FSR_FFX_INTEGRATION.md` deve incorporar este desenho).

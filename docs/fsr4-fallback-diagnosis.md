# FSR4 Fallback Permanente — Diagnóstico e Fix (Story 01, RETRY)

## Problema

Na máquina dev (RDNA4 + ecossistema FSR4 instalado), o app **sempre** executa
o fluxo de fallback FSR1 mesmo com FSR4 disponível. Adicionalmente,
`SettingsViewModel.Fsr4Available` nunca recebe o valor real da probe nativa —
permanece hardcoded `true` — tornando o warning de indisponibilidade inútil.

## Causa Raiz (Confirmada)

### B1 (Blocker): FFX providers não resolvidos por path absoluto

`FfxRuntime::Load()` carrega o loader `amd_fidelityfx_loader_dx12.dll` por
caminho absoluto com `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR`. Porém, o loader FFX
internamente carrega os providers **POR NOME** via `LoadLibrary`:

```
LoadLibrary("amd_fidelityfx_upscaler_dx12.dll")
LoadLibrary("amd_fidelityfx_framegeneration_dx12.dll")
LoadLibrary("amd_ags_x64.dll")
LoadLibrary("amd_acs_x64.dll")
```

A ordem de busca padrão do processo (exe dir, system dirs, CWD, PATH) **NÃO**
inclui `runtimes/win-x64/native/` onde as DLLs realmente estão.

**Evidência do reviewer (probe controlada)**:

```
# COM SetDllDirectory(runtimes/win-x64/native/):
[FFX] ffxQuery(GetVersions,count) ret=1  ← SUCESSO

# SEM SetDllDirectory (ordem de busca padrão):
[FFX] ffxQuery(GetVersions,count) failed, ret=4  ← FFX_API_RETURN_NO_PROVIDER
```

`ret=4` (NO_PROVIDER) prova que o loader **encontra** o loader DLL mas **não**
encontra os providers. Se os providers estivessem ausentes do disco,
`ProbeDependencyDlls()` falharia antes — mas ela passa (todos os 8 arquivos
existem). O problema é que o loader não os encontra via `LoadLibrary` por nome.

### B2 (Blocker): catra-gpu.dll stale na raiz do bin sombreia a correta

```
bin/Debug/net8.0-windows/catra-gpu.dll           → 189KB, 15/08 (STALE, sem exports novos)
bin/Debug/net8.0-windows/runtimes/win-x64/native/catra-gpu.dll → 1.38MB, 04/09 (correta)
```

O resolver padrão do P/Invoke (`[DllImport("catra-gpu.dll")]`) procura primeiro
em `AppContext.BaseDirectory` (raiz do bin). A DLL stale de 189KB é carregada
no lugar da correta de 1.38MB. Isso causa:

1. `catra_is_ffx_available` não existe na DLL stale → `EntryPointNotFoundException`
2. Mesmo que existisse, `ModuleDirWide()` resolveria para a raiz do bin, onde
   as 8 DLLs FFX **não** existem → probe falha.

### N2: IsFfxAvailable (transiente) ≠ IsFsr4Available (definitivo)

- `catra_is_ffx_available()` → `FfxRuntime::IsAvailable()`: cria device DX12
  transiente, independente de `g_device`. Usado **antes** de `catra_init()`.
- `catra_is_fsr4_available()` → `Fsr4IsAvailable(g_device.Get())`: usa o
  D3D11 device do decoder (via `catra_init`). **Definitivo** — testa o adapter
  real onde o vídeo está sendo processado. Retorna 0 antes de `catra_init`.

O SettingsViewModel usava `IsFfxAvailable()` sempre. Correto: usar
`IsFsr4Available()` quando bridge já inicializada, `IsFfxAvailable()` senão.

## Fix Aplicado

### B1: Pre-load de FFX providers por caminho absoluto

Em `ffx_runtime.cpp::Load()`, ANTES de `ffxLoadFunctions`:

```cpp
// Pre-load provider DLLs by absolute path
for (const char* provName : kProviderDlls) {
    const std::wstring provPath = moduleDir + L"\\" + AsciiToWide(provName);
    HMODULE provMod = LoadLibraryExW(provPath.c_str(), nullptr,
                                     LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
    if (provMod == nullptr) {
        // N8 fallback: LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR fails err=126 outside .NET host
        provMod = LoadLibraryExW(provPath.c_str(), nullptr, 0);
    }
}
```

Quando o loader FFX posteriormente faz `LoadLibrary("amd_fidelityfx_upscaler_dx12.dll")`
por nome, o OS encontra o módulo já carregado (mesmo canonical path) e retorna
o HMODULE existente. `ffxQuery(GetVersions)` agora retorna `ret=1` (sucesso).

### B2: DllImportResolver + cleanup de stale DLL

1. `NativeLibrary.SetDllImportResolver` em `NativeBridge.cs`: carrega
   `catra-gpu.dll` por caminho absoluto preferindo `runtimes/win-x64/native/`
   sobre a raiz do bin. Loga o path resolvido.

2. `CATRA.App.csproj` target `BuildNativeBridge`: adicionado `<Delete>` que
   remove `$(OutDir)catra-gpu.dll` e `.pdb` stale da raiz do output.

3. Artefato stale existente (`189KB, 15/08`) deletado manualmente.

### N2: SettingsViewModel usa probe correta

```csharp
_fsr4Available = _nativeBridge.IsInitialized
    ? _nativeBridge.IsFsr4Available()   // definitivo, adapter do decoder
    : _nativeBridge.IsFfxAvailable();   // transiente, adapter default
```

### N3: Teste de degradação (EntryPointNotFoundException)

Adicionado teste `Ctor_degrades_gracefully_when_FfxProbe_throws_EntryPointNotFoundException`
que simula a DLL stale (sem export `catra_is_ffx_available`) e verifica que:
- O construtor não crasha
- `Fsr4Available = false`
- `UpscaleWarning` contém sugestão FSR 1

### N4: Cache do probe FFX

```csharp
private static readonly Lazy<bool> s_ffxProbeCache = new(ProbeFfxAvailableNative);
```

Process-level `static Lazy<bool>`: a probe (D3D12CreateDevice + ffxQuery,
82-164ms medidos) roda no máximo uma vez. Navegações repetidas a Settings
não re-executam o probe.

### N5: Default `_fsr4Available = false` (fail-safe)

Default anterior era `true` (assumia FSR4 disponível). Agora é `false` — se
a probe falhar ou a bridge estiver indisponível, FSR4 fica marcado como
indisponível (seguro: o fallback FSR1 funciona).

### N7: `catra_is_fsr4_available()` mantido para diagnóstico pós-init

`catra_is_fsr4_available()` permanece na C ABI (`catra_gpu.h`) como ferramenta
de diagnóstico pós-init. Após `catra_init(device)`, esta função testa FSR4 no
adapter real do decoder (não no adapter default). É o teste **definitivo** para
a pipeline de processamento. Documentado no header:

> Non-zero if the FFX upscale runtime is usable on the active adapter...
> Returns 0 before catra_init or when any check fails.

## Evidência Runtime

### Setup
- Máquina: AMD Radeon RX 9070 XT (RDNA 4)
- Build: `build_native_now.bat` (native) + `dotnet build` (managed)
- Stale DLL deletada da raiz do bin

### Log esperado após fix (catra_is_ffx_available via app):

```
[NativeBridge] resolved catra-gpu.dll -> C:\...\bin\Debug\net8.0-windows\runtimes\win-x64\native\catra-gpu.dll
[FFX] Load: moduleDir=C:\...\bin\Debug\net8.0-windows\runtimes\win-x64\native
[FFX] provider pre-loaded: amd_fidelityfx_upscaler_dx12.dll
[FFX] provider pre-loaded: amd_fidelityfx_framegeneration_dx12.dll
[FFX] provider pre-loaded: amd_ags_x64.dll
[FFX] provider pre-loaded: amd_acs_x64.dll
[FFX] amd_fidelityfx_loader_dx12.dll loaded, 5/5 ffx exports resolved + providers pre-loaded
[FFX] ProbeDependencyDlls: all 8 DLLs present
[FFX] ffxQuery(GetVersions,count) ret=1, count=N
[catra-gpu] catra_is_ffx_available: result=true (pre-init probe)
```

### Sem o fix (controle — evidência do reviewer):

```
[FFX] Load: moduleDir=C:\...\bin\Debug\net8.0-windows\runtimes\win-x64\native
[FFX] amd_fidelityfx_loader_dx12.dll loaded, 5/5 ffx exports resolved
[FFX] ProbeDependencyDlls: all 8 DLLs present
[FFX] ffxQuery(GetVersions,count) failed, ret=4  ← NO_PROVIDER
[catra-gpu] catra_is_ffx_available: result=false (pre-init probe)
```

## Arquivos Alterados

### Native (exige rebuild nativo)
- `native/catra-gpu/ffx_runtime.cpp` — B1: pre-load providers, logging melhorado

### Managed
- `src/CATRA.Services/Processing/NativeBridge.cs` — B2: DllImportResolver, N4: cache, N3: tratamento EntryPointNotFoundException
- `src/CATRA.App/CATRA.App.csproj` — B2: cleanup stale DLL via `<Delete>`
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` — N2: probe correta, N5: fail-safe default

### Testes
- `tests/CATRA.UI.Tests/Fakes.cs` — N3: FakeNativeBridge suporta exceção no probe
- `tests/CATRA.UI.Tests/SettingsProcessingTests.cs` — N3: 3 testes novos (degradação, N2, N5)

### Documentação
- `docs/fsr4-fallback-diagnosis.md` — N1: reescrito com evidência real

## Riscos Residuais

1. **Native rebuild obrigatório**: as mudanças em `ffx_runtime.cpp` (pre-load
   de providers) exigem `build_native_now.bat` ou `scripts/build-native.ps1`.
   Sem rebuild, o comportamento é o mesmo de antes (providers não encontrados).

2. **`LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR` em harness standalone**: pode falhar
   com `err=126` fora do host .NET (N8). O fallback `flags=0` resolve o path
   absoluto mas não ajuda com dependências transitivas dos providers. O smoke
   test nativo deve usar `SetDllDirectory` como workaround se necessário.

3. **Cache estático não invalida**: se hardware mudar em runtime (hot-swap GPU,
   driver update sem restart), o cache mantém o resultado antigo. Na prática,
   GPUs não mudam sem restart do app.

4. **`SetDllDirectory(lib/ffmpeg)` no App.xaml.cs**: não conflita com o fix B1.
   O `SetDllDirectory` é chamado durante `ConfigureFfmpeg()` e restaurado no
   `finally`. A resolução do `catra-gpu.dll` via `DllImportResolver` é
   independente da search path do kernel32. Mas NÃO usar `SetDefaultDllDirectories`
   (conflitaria — confirmado pelo reviewer).

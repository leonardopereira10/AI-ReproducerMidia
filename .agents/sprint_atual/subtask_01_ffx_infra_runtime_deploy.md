# Subtask 01: Infra FFX — runtime loader (`ffx_runtime`), smoke test das 8 DLLs e deploy runtime

**Story:** story_01_ffx_infra_runtime_deploy.md
**Tipo:** dev
**Complexidade:** alta
**Agente:** developer-alta

> Justificativa da complexidade (skill complexity-eval): código nativo C++/Win32
> (LoadLibrary/GetProcAddress), wiring de build em 3 camadas (CMake install →
> `native/catra-gpu/install/runtimes/win-x64/native/` → `CATRA.App.csproj` →
> saída do .NET), matriz completa de edge cases (DLLs ausentes, símbolos
> parciais, version mismatch, concorrência) e smoke test nativo E2E de carga.

## Descrição

Fundação runtime-loaded da integração FidelityFX (FFX API 2.x), **sem nenhuma
dependência de build-time em SDK FSR** (nenhum `.lib`, apenas
`LoadLibrary`/`GetProcAddress`). Entrega:

1. **Módulo nativo `ffx_runtime`** em `native/catra-gpu/`: carga do loader
   `amd_fidelityfx_loader_dx12.dll`, resolução das 5 funções FFX
   (`ffxCreateContext`, `ffxDestroyContext`, `ffxConfigure`, `ffxQuery`,
   `ffxDispatch` — struct `ffxFunctions` + padrão `ffxLoadFunctions` de
   `ffx_api_loader.h`), capability probe (loader + 8 DLLs no disco + adapter
   DX12), query de versão fail-safe e bridge de log FFX→`BackendLog`.
2. **Smoke test nativo** (`catra-ffx-load-test`) que carrega as **8 DLLs** da
   lista A1 e valida o query de versão FFX 2.3.x; exit code ≠ 0 em qualquer falha.
3. **Deploy das DLLs runtime** junto ao `catra-gpu.dll`
   (`install/runtimes/win-x64/native/`) e na saída do .NET (csproj copy), com
   atribuição MIT/copyright AMD ao lado das DLLs (A7).

**Pré-condição já executada pela orquestração (NÃO re-baixar / NÃO re-vendorar):**
headers FFX API 2.3.0 já estão em `lib/FidelityFX-SDK-2.3.0/`
(`api/include/`, `upscalers/include/`, `framegeneration/include/`,
`reference/fsrapirendermodule.cpp(.h)`, `README.md`, `sdk-readme.md`). Esta
subtask faz apenas o **wiring de build** desse material. As DLLs de runtime vêm
de `lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/`
(pacote do usuário — confirmado por inspeção que contém todas as 8).

## Arquivos Alvo (fileScope)

**Criar:**
- `native/catra-gpu/ffx_runtime.h`
- `native/catra-gpu/ffx_runtime.cpp`
- `native/catra-gpu/tools/ffx_load_smoke_test.cpp`
- `lib/FidelityFX-SDK-2.3.0/LICENSE` — texto MIT completo + `Copyright (C) 2026 Advanced Micro Devices, Inc.` (o diretório vendorado ainda não tem LICENSE; os headers já trazem o notice no topo)
- `docs/fsr/README.md` — ponteiro curto para a referência oficial já vendorada em `lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp(.h)` + link do manual (https://gpuopen.com/manuals/fsr_sdk/). *Desvio do fileScope preliminar da story:* o sample `.cpp` NÃO será duplicado em `docs/fsr/` — já está vendorado em `lib/FidelityFX-SDK-2.3.0/reference/` pela orquestração; duplicar 1882 linhas violaria a nota da orquestração ("usar esse material, não re-baixar").

**Modificar:**
- `native/catra-gpu/CMakeLists.txt` — include dirs dos headers vendorados; `ffx_runtime.cpp` nas sources do `catra-gpu`; target do smoke test com POST_BUILD copy das 8 DLLs; install rules das 8 DLLs + `FFX-LICENSE.txt` em `runtimes/win-x64/native`
- `src/CATRA.App/CATRA.App.csproj` — copy das 8 DLLs + `FFX-LICENSE.txt` de `$(CatraNativeDir)` para `$(OutDir)runtimes\win-x64\native` (mesmo destino do `catra-gpu.dll`), espelhando o padrão `_OnnxDlls` do target `BuildNativeBridge`

**NÃO tocar:** `upscale_fsr4.cpp`, `encode_amf.cpp`, `catra_gpu.h` (docstring de
`catra_is_fsr4_available` será atualizada na story 02), nenhum `.cs` de
Services/UI (stories 02–05), arquivos `bugfix_*.json`. Commits apenas deste
fileScope (repo tem arquivos sujos fora do escopo — **nunca `git add .`**).

## Passos

### 1. `lib/FidelityFX-SDK-2.3.0/LICENSE`
Texto MIT padrão com `Copyright (C) 2026 Advanced Micro Devices, Inc.` (mesmo
notice dos headers, ex.: `api/include/ffx_api.h` linhas 1–22).

### 2. `native/catra-gpu/ffx_runtime.h` / `ffx_runtime.cpp`

API interna C++ (namespace `catra::ffx`, NÃO exportada na ABI flat — as stories
02/03 consomem; nada de `__declspec(dllexport)` novo):

```cpp
namespace catra::ffx {
class FfxRuntime {
public:
    // LoadLibraryA("amd_fidelityfx_loader_dx12.dll") + ffxLoadFunctions.
    // Idempotente (std::once). false se DLL ausente ou qualquer um dos 5
    // GetProcAddress retornar NULL (nesse caso faz FreeLibrary e desfaz).
    static bool Load();
    static void Unload();              // FreeLibrary; idempotente; chamado por catra_shutdown (story 02 faz o wiring)
    static bool IsLoaded();
    static const ffxFunctions& Functions();   // válidas só com IsLoaded()==true
    static bool ProbeDependencyDlls(); // as 8 DLLs da lista A1 existem no module dir
    static bool ProbeDx12Adapter();    // D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0) em device temporário
    static bool IsAvailable();         // Load() && ProbeDependencyDlls() && ProbeDx12Adapter() && sem mismatch
    // ffxQueryDescGetVersions (FFX_API_QUERY_DESC_TYPE_GET_VERSIONS=4u) com
    // createDescType=FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE e o device DX12
    // passado; 2 passagens (outputCount=0 → capacidade; depois preenche
    // versionIds/versionNames). Retorna nº de versões; 0 = falha/mismatch.
    static int QueryUpscaleVersions(ID3D12Device* device, std::vector<std::string>& outNames);
    static bool InstallLogBridge();    // ffxConfigure(nullptr, GlobalDebug) — ver passo 2d
};
}
```

Detalhes obrigatórios (edge cases mapeados):

- **2a. Pegadinha `_WINDOWS`**: `ffxLoadFunctions` em
  `lib/FidelityFX-SDK-2.3.0/api/include/ffx_api_loader.h` tem o corpo inteiro
  dentro de `#if defined(_GAMING_XBOX) || defined(_WINDOWS) || defined(PLATFORM_WINDOWS)`
  — e o MSVC **não define `_WINDOWS`** automaticamente. Em `ffx_runtime.cpp`,
  ANTES de `#include <ffx_api_loader.h>`:
  `#ifndef _WINDOWS` / `#define _WINDOWS` / `#endif` (o mesmo guard passa a
  incluir `<libloaderapi.h>`). Sem isso os 5 ponteiros ficam silenciosamente NULL.
- **2b. Resolução de caminhos**: diretório das DLLs irmãs = diretório do próprio
  `catra-gpu.dll` via `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
  ..._UNCHANGED_REFCOUNT, (LPCWSTR)&FfxRuntime::Load, &hmod)` +
  `GetModuleFileNameW` (precedente existente: `interp_rife.cpp` linhas 151–212
  usa o mesmo truço do endereço de função como proxy do módulo). `ProbeDependencyDlls`
  testa `GetFileAttributesA(dir + "\\" + nome)` das 8 DLLs (lista A1 abaixo).
  `LoadLibraryA("amd_fidelityfx_loader_dx12.dll")` resolve pelo search order
  padrão (exe/module dir primeiro) — o deploy garante co-localização.
- **2c. Fail-safe de versão**: `QueryUpscaleVersions` com erro (`ffxReturnCode_t
  != FFX_API_RETURN_OK`) ou `count == 0` → log warn e retorna 0; caller
  (`IsAvailable`) trata 0 como indisponível — sem crash, sem exceção.
- **2d. Bridge de log FFX→BackendLog**: callback estático
  `static void FfxLogSink(uint32_t type, const wchar_t* msg)`; converte
  `wchar_t`→UTF-8 com `WideCharToMultiByte(CP_UTF8, ...)` e chama
  `catra::BackendLog` (definido em `catra_gpu.cpp:145`, forward-declare como em
  `d3d_interop.cpp:22`): `FFX_API_MESSAGE_TYPE_ERROR`→`CATRA_LOG_ERROR`,
  `FFX_API_MESSAGE_TYPE_WARNING`→`CATRA_LOG_WARN`. Instalação via
  `ffxConfigureDescGlobalDebug` (`header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG`
  = 7u, `effectId = FFX_API_EFFECT_ID_GENERAL`, `fpMessage = FfxLogSink`,
  `debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_WARNINGS`) com
  `Functions().Configure(nullptr, &desc)` (context NULL = estado global).
  Callback é chamado de threads do FFX → `BackendLog` já é thread-safe
  (`g_logMutex` em `catra_gpu.cpp:45`); NÃO alocar/lançar dentro do sink.
- **2e. Concorrência**: `Load`/`Unload`/flags protegidos por `std::mutex`
  (mesmo padrão de `g_logMutex`); `Load` duplo = no-op; `Unload` com DLL não
  carregada = no-op. Nenhuma exceção C++ atravessa o sink ou os probes.
- **2f. Compila limpo com `/W4 /WX /permissive- /utf-8`** (flags do target
  `catra-gpu` em `CMakeLists.txt`).

Includes do `.cpp`: `<windows.h>` (com `WIN32_LEAN_AND_MEAN`), `<libloaderapi.h>`,
`<d3d12.h>`, `<vector>`, `<string>`, `<mutex>` + headers FFX `<ffx_api.h>`,
`<ffx_api_loader.h>`, `<ffx_upscale.h>` (para `FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE`).

### 3. `native/catra-gpu/tools/ffx_load_smoke_test.cpp` (smoke test das 8 DLLs)

`int main()` standalone (estilo `tools/interop_readback_test.cpp`):

1. Para cada uma das **8 DLLs** (lista A1): `LoadLibraryA(nome)`; imprimir
   OK/FALHA com `GetLastError()`; qualquer falha → `return 1`. **As DLLs devem
   estar co-localizadas com o .exe** (POST_BUILD copy do passo 4 garante):
   `amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`,
   `amd_fidelityfx_framegeneration_dx12.dll`, `amd_ags_x64.dll`,
   `amd_acs_x64.dll`, `D3D12Core.dll`, `dxcompiler.dll`, `dxil.dll`.
2. `catra::ffx::FfxRuntime::Load()` + verificar os 5 ponteiros de
   `Functions()` não-NULL (`ffxCreateContext`, `ffxDestroyContext`,
   `ffxConfigure`, `ffxQuery`, `ffxDispatch`); falha → `return 2`.
3. Device DX12 transitório: `D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0,
   IID_PPV_ARGS(&device))` (fallback: FEATURE_LEVEL_12_0 → falhar com msg se
   nenhum adapter DX12; `return 3`).
4. `QueryUpscaleVersions(device, names)` com
   `createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE`; imprimir count e
   nomes; exigir count ≥ 1 e algum nome contendo `"2.3"` (mismatch → `return 4`).
5. `FfxRuntime::InstallLogBridge()` e um `Configure` de smoke (aceitar
   `FFX_API_RETURN_OK` ou código de erro não-fatal já loggado).
6. `FreeLibrary` das 8 (ordem reversa), `device->Release()`, imprimir `PASS`,
   `return 0`.

### 4. `native/catra-gpu/CMakeLists.txt`

- **Variáveis** (após o bloco de vcpkg):
  ```cmake
  set(CATRA_FFX_SDK_HEADERS "${CMAKE_SOURCE_DIR}/../../lib/FidelityFX-SDK-2.3.0"
      CACHE PATH "Vendored FidelityFX SDK 2.3.0 headers (MIT)")
  set(CATRA_FFX_DLL_DIR "${CMAKE_SOURCE_DIR}/../../lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release"
      CACHE PATH "Prebuilt FFX runtime DLLs (samples package)")
  set(CATRA_FFX_DLLS amd_fidelityfx_loader_dx12.dll amd_fidelityfx_upscaler_dx12.dll
      amd_fidelityfx_framegeneration_dx12.dll amd_ags_x64.dll amd_acs_x64.dll
      D3D12Core.dll dxcompiler.dll dxil.dll)
  ```
- **Include dirs** (condicional `EXISTS "${CATRA_FFX_SDK_HEADERS}/api/include/ffx_api.h"`;
  senão `message(FATAL_ERROR)` — sem headers o módulo não compila):
  `target_include_directories(catra-gpu PRIVATE "${CATRA_FFX_SDK_HEADERS}/api/include"
  "${CATRA_FFX_SDK_HEADERS}/upscalers/include" "${CATRA_FFX_SDK_HEADERS}/framegeneration/include")`.
  Nota: os includes relativos internos (`ffx_upscale.h` → `"../../api/include/ffx_api.h"`)
  resolvem pelo layout vendorado que espelha o SDK — não mexer no layout de `lib/FidelityFX-SDK-2.3.0/`.
- **Sources**: adicionar `ffx_runtime.cpp` ao `add_library(catra-gpu SHARED ...)`
  (lista atual: `catra_gpu.cpp d3d_interop.cpp encode_amf.cpp interp_rife.cpp
  nv12_to_bgra_shader.cpp upscale_fsr1.cpp upscale_fsr4.cpp`). **Nenhum `.lib`
  FFX em `target_link_libraries`** — runtime-loaded apenas.
- **Target do smoke test** (não instalado, padrão `catra-interop-test`):
  ```cmake
  add_executable(catra-ffx-load-test tools/ffx_load_smoke_test.cpp ffx_runtime.cpp)
  target_include_directories(catra-ffx-load-test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} <3 dirs FFX>)
  target_link_libraries(catra-ffx-load-test PRIVATE d3d12 dxgi dxguid)
  # /W3 /utf-8 /EHsc como catra-interop-test
  foreach(_dll IN LISTS CATRA_FFX_DLLS)
      add_custom_command(TARGET catra-ffx-load-test POST_BUILD
          COMMAND ${CMAKE_COMMAND} -E copy_if_different
                  "${CATRA_FFX_DLL_DIR}/${_dll}" "$<TARGET_FILE_DIR:catra-ffx-load-test>/${_dll}"
          COMMAND_EXPAND_LISTS)
  endforeach()
  ```
  (guardar com `if(EXISTS "${CATRA_FFX_DLL_DIR}")`, senão warning — sem o
  pacote do usuário o target compila mas o teste não roda.)
- **Install rules** (ao lado do bloco de install do ONNX, final do arquivo):
  ```cmake
  foreach(_dll IN LISTS CATRA_FFX_DLLS)
      install(FILES "${CATRA_FFX_DLL_DIR}/${_dll}"
              DESTINATION runtimes/win-x64/native OPTIONAL)
  endforeach()
  install(FILES "${CATRA_FFX_SDK_HEADERS}/LICENSE"
          DESTINATION runtimes/win-x64/native RENAME FFX-LICENSE.txt OPTIONAL)
  ```
  `OPTIONAL` mantém `cmake --install` verde em clone sem o pacote prebuilt.

### 5. `src/CATRA.App/CATRA.App.csproj`

No target `BuildNativeBridge` existente, após o bloco `_OnnxDlls`, espelhando o
mesmo padrão (Condition por Exists + `SkipUnchangedFiles`):

```xml
<ItemGroup Condition="'$(NativeBuildEnabled)' == 'true' And Exists('$(CatraNativeDir)\amd_fidelityfx_loader_dx12.dll')">
  <_FfxDlls Include="$(CatraNativeDir)\amd_fidelityfx_loader_dx12.dll;$(CatraNativeDir)\amd_fidelityfx_upscaler_dx12.dll;$(CatraNativeDir)\amd_fidelityfx_framegeneration_dx12.dll;$(CatraNativeDir)\amd_ags_x64.dll;$(CatraNativeDir)\amd_acs_x64.dll;$(CatraNativeDir)\D3D12Core.dll;$(CatraNativeDir)\dxcompiler.dll;$(CatraNativeDir)\dxil.dll;$(CatraNativeDir)\FFX-LICENSE.txt" />
</ItemGroup>
<Copy SourceFiles="@(_FfxDlls)" DestinationFolder="$(CatraOutputNativeDir)"
      SkipUnchangedFiles="true"
      Condition="'$(NativeBuildEnabled)' == 'true' And Exists('$(CatraNativeDir)\amd_fidelityfx_loader_dx12.dll')" />
```

(`$(CatraNativeDir)` = `native/catra-gpu/install/runtimes/win-x64/native` — já
definido no csproj; destino `$(OutDir)runtimes\win-x64\native` = mesmo diretório
do `catra-gpu.dll`, onde o loader resolve as DLLs irmãs.)

### 6. `docs/fsr/README.md`

3–10 linhas: referência oficial do sample em
`lib/FidelityFX-SDK-2.3.0/reference/fsrapirendermodule.cpp(.h)` (read-only),
origem (GPUOpen FidelityFX-SDK tag v2.3.0, MIT), link do manual e ponteiro para
o plano `.agents/specs/fsr-sdk-integration_plan.md`.

### 7. Validação (build-gate)

1. `pwsh ./scripts/build-native.ps1 -Configuration Release` → configure/build/
   install OK; verificar `install/runtimes/win-x64/native/` com `catra-gpu.dll`
   + 8 DLLs FFX + `FFX-LICENSE.txt`.
2. Rodar smoke test: `native/catra-gpu/build/Release/catra-ffx-load-test.exe` →
   `PASS`, exit code 0, 8/8 DLLs carregadas, nome de versão contendo `2.3`.
3. Teste de ausência (edge case): mover/renomear uma DLL do diretório do exe do
   smoke test → exit code ≠ 0, mensagem clara, sem crash (valida fallback).
4. `dotnet build CATRA.sln` → verde; conferir as 8 DLLs + `FFX-LICENSE.txt` em
   `src/CATRA.App/bin/<Config>/net8.0-windows/runtimes/win-x64/native/`.
5. `dotnet test` → sem regressão.

## Critérios de Aceite

- [ ] `dotnet build CATRA.sln` e build nativo passam **sem** nenhum SDK FSR em
      build-time: `ffx_runtime` usa só `LoadLibraryA`/`GetProcAddress` (nenhum
      `.lib` FFX em `target_link_libraries`).
- [ ] Smoke test (`catra-ffx-load-test`) carrega as **8 DLLs** da lista A1,
      resolve as 5 funções FFX e reporta versão FFX 2.3.x via
      `ffxQueryDescGetVersions`; exit code ≠ 0 se qualquer DLL falhar.
- [ ] Com DLL ausente em runtime: probe retorna indisponível e o processo não
      crash (verificado pelo teste de ausência do passo 7.3).
- [ ] 8 DLLs + `FFX-LICENSE.txt` presentes em
      `native/catra-gpu/install/runtimes/win-x64/native/` E na saída do .NET
      (`bin/.../runtimes/win-x64/native/`).
- [ ] Atribuição MIT/copyright AMD presente: `lib/FidelityFX-SDK-2.3.0/LICENSE`
      criado, headers já trazem notice no topo, `FFX-LICENSE.txt` instalado junto
      das DLLs (A7).
- [ ] `ffx_runtime.cpp` compila limpo com `/W4 /WX /permissive-`; include de
      `ffx_api_loader.h` protegido pelo `#define _WINDOWS` (pegadinha 2a).
- [ ] `dotnet test` passa sem regressão.
- [ ] Commit contém APENAS arquivos do fileScope (nunca `git add .`).

## Dependências

- Nenhuma subtask (story base do sprint SPRINT_04).
- Insumos já presentes no repo (pré-executados pela orquestração/usuário):
  headers em `lib/FidelityFX-SDK-2.3.0/` e DLLs prebuilt em
  `lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/`
  (verificado por inspeção: as 8 da lista A1 existem, exceto `.pdb`/`.exe` que
  não entram no deploy).
- Bloqueia (consumidores): story 02 (upscale FSR 4/3.1 via `ffx_runtime`),
  story 03 (FG), demais stories de UI/settings.

# Story 01 — Infra FFX: vendor de headers, runtime loader e deploy das DLLs

**Sprint:** SPRINT_04 · **Spec/Plano:** `.agents/specs/fsr-sdk-integration_plan.md` (aprovado 2026-08-13)
**Tipo:** dev

## Descrição

Fundação de toda a integração FidelityFX (FFX API 2.x). Entrega três coisas, todas
runtime-loaded (nenhuma dependência de build-time em SDK FSR):

1. **Vendor dos headers FFX API 2.3.0 (MIT)** — o pacote do usuário
   (`lib/FidelityFX-Samples-v2.3.0-prebuilt/`) NÃO contém headers; vendorar do
   FidelityFX-SDK v2.3.0 (GitHub `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK`, tag v2.3.0):
   `api/include/` (ffx_api.h/hpp, ffx_api_loader.h, ffx_api_types.h, dx12/),
   `upscalers/include/` (ffx_upscale.h/hpp) e `framegeneration/include/`
   (ffx_framegeneration.h/hpp, dx12/). Manter atribuição MIT (LICENSE/nota de copyright
   AMD) junto aos arquivos (**A7**).
2. **Módulo nativo `ffx_runtime`** no catra-gpu: `LoadLibrary("amd_fidelityfx_loader_dx12.dll")` +
   `GetProcAddress` das 5 funções FFX (`ffxCreateContext`, `ffxDestroyContext`, `ffxConfigure`,
   `ffxQuery`, `ffxDispatch` — struct `ffxFunctions` + padrão `ffxLoadFunctions` de
   `ffx_api_loader.h`), capability probe (loader + DLLs + adapter DX12), query de versão
   (fail-safe em mismatch) e bridge de log FFX→BackendLog.
3. **Deploy das DLLs runtime** junto ao catra-gpu.dll e na saída do .NET (CMake install +
   csproj copy). Lista completa (**A1**): `amd_fidelityfx_loader_dx12.dll`,
   `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_framegeneration_dx12.dll`,
   `amd_ags_x64.dll`, `amd_acs_x64.dll`, `D3D12Core.dll` (Agility), `dxcompiler.dll`,
   `dxil.dll`.

Inclui ainda o vendor do sample oficial `fsrapirendermodule.cpp` como referência
read-only em `docs/fsr/`, e um **smoke test nativo** que valida a carga de TODAS as 8 DLLs.

## FileScope (preliminar)

**Criar:**
- `lib/FidelityFX-SDK-v2.3.0-headers/api/include/**` (ffx_api.h, ffx_api.hpp, ffx_api_loader.h, ffx_api_types.h, dx12/)
- `lib/FidelityFX-SDK-v2.3.0-headers/upscalers/include/**` (ffx_upscale.h/hpp)
- `lib/FidelityFX-SDK-v2.3.0-headers/framegeneration/include/**` (ffx_framegeneration.h/hpp, dx12/)
- `lib/FidelityFX-SDK-v2.3.0-headers/LICENSE` (MIT + copyright AMD)
- `native/catra-gpu/ffx_runtime.h` / `ffx_runtime.cpp`
- `native/catra-gpu/tools/ffx_load_smoke_test.cpp`
- `docs/fsr/fsrapirendermodule.cpp` (referência read-only) + `docs/fsr/README.md`

**Modificar:**
- `native/catra-gpu/CMakeLists.txt` — include dirs dos headers vendorados; target do smoke test; install rules das 8 DLLs de `lib/FidelityFX-Samples-v2.3.0-prebuilt/`
- `src/CATRA.App/CATRA.App.csproj` — copy das 8 DLLs para a saída (mesmo caminho do catra-gpu.dll)

**NÃO tocar:** `upscale_fsr4.cpp`, `encode_amf.cpp`, arquivos C# (stories 02–05);
arquivos `bugfix_*.json` do sprint.

## Critérios de Aceite

- [ ] `dotnet build CATRA.sln` e build nativo passam **sem** nenhum SDK FSR em build-time
      (loader usa apenas LoadLibrary/GetProcAddress; nenhum .lib).
- [ ] Smoke test nativo carrega com sucesso **todas as 8 DLLs** da lista A1 e reporta
      versão FFX 2.3.x via query; exit code ≠ 0 se qualquer DLL falhar.
- [ ] Com as DLLs ausentes em runtime: probe retorna "indisponível" e o app segue
      funcionando (fallback: upscale FSR 1, FG indisponível) **sem crash**.
- [ ] DLLs presentes na pasta de saída do .NET após build (CMake install + csproj copy).
- [ ] Atribuição MIT/copyright AMD presente junto aos headers vendorados e DLLs (A7).
- [ ] Testes existentes passam sem regressão.

## Dependências

- Nenhuma (story base do sprint).

## Notas

- Origem das DLLs: `lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/`.
- Risco mitigado: version mismatch headers×DLLs é baixo (mesma origem SDK 2.3.0) e validado por query.
- Commits apenas dos arquivos do fileScope (repo tem arquivos sujos fora do escopo — nunca `git add .`).

## Nota da Orquestração (prep já executado)

Os headers FFX API 2.3.0 e a referência do sample **já foram vendorizados** pela orquestração
em `lib/FidelityFX-SDK-2.3.0/` (ver `README.md` do diretório): `api/include/`,
`upscalers/include/`, `framegeneration/include/`, `reference/fsrapirendermodule.cpp(.h)`,
`sdk-readme.md`. A subtask deve USAR esse material (wiring no build), não re-baixar.

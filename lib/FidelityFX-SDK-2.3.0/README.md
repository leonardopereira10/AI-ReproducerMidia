# FidelityFX SDK 2.3.0 ("Redstone") — material vendorizado (headers + referência)

Conteúdo extraído do release oficial `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` tag v2.3.0
(licença MIT — cada header carrega o notice de copyright AMD no topo).

## Layout

- `api/include/` — FFX API 2.x core: `ffx_api.h`, `ffx_api.hpp`, `ffx_api_loader.h`
  (padrão LoadLibrary/GetProcAddress), `ffx_api_types.h`, `dx12/ffx_api_dx12.h(.hpp)`.
- `upscalers/include/` — `ffx_upscale.h(.hpp)` (FFSR Upscaler: FSR 4.1 ML / FSR 3.1).
- `framegeneration/include/` — `ffx_framegeneration.h(.hpp)`,
  `ffx_framegeneration_api_types.h`, `dx12/ffx_api_framegeneration_dx12.h(.hpp)`
  (FG context + FG SwapChain DX12, inclusive criação por HWND).
- `reference/fsrapirendermodule.cpp(.h)` — módulo do sample oficial FidelityFX_FSR
  (referência read-only de integração: criação de contextos, callbacks, swapchain FG).
- `sdk-readme.md` — readme original do SDK 2.3.0.

## Runtime DLLs (NÃO estão aqui)

As DLLs de runtime vêm do pacote de samples prebuilt baixado pelo usuário:
`lib/FidelityFX-Samples-v2.3.0-prebuilt/Samples/Upscalers/FidelityFX_FSR/dx12/x64/Release/`

Lista de deploy: `amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`,
`amd_fidelityfx_framegeneration_dx12.dll`, `amd_ags_x64.dll`, `amd_acs_x64.dll`,
`D3D12Core.dll`, `dxcompiler.dll`, `dxil.dll`.

## Documentação

- Manual oficial: https://gpuopen.com/manuals/fsr_sdk/
- Plano de integração: `.agents/specs/fsr-sdk-integration_plan.md`

© Advanced Micro Devices, Inc. MIT License — ver cabeçalho de cada arquivo.

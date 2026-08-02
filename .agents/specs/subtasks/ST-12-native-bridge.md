# Subtask 12: Native Bridge C++ Skeleton

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Integração Native Bridge)
Depende de ST-01 (estrutura do repo).

## Objetivo
DLL C++ com API C flat, build system CMake + vcpkg, P/Invoke do C#.
Skeleton com lifecycle (init/shutdown) e stubs para interp/upscale/encode.
Esta subtask NÃO implementa FSR/RIFE/AMF — só a infraestrutura de bridge.

## Escopo
### Arquivos a Criar
- `native/catra-gpu/CMakeLists.txt`
- `native/catra-gpu/catra_gpu.h` — C ABI header (API completa da spec)
- `native/catra-gpu/catra_gpu.cpp` — impl skeleton (stubs retornam erro)
- `native/catra-gpu/d3d_interop.h` — header D3D11↔DX12 (stub)
- `native/catra-gpu/d3d_interop.cpp`
- `native/catra-gpu/vcpkg.json` — dependências (directx-headers, dxguid)
- `src/CATRA.Services/Processing/NativeBridge.cs` — P/Invoke wrapper C#
- `src/CATRA.Services/Processing/NativeBridgeException.cs`
- `src/CATRA.Core/Interfaces/INativeBridge.cs`
- `tests/CATRA.Services.Tests/Processing/NativeBridgeTests.cs`
- `scripts/build-native.ps1` — build script (cmake + vcpkg)

### Arquivos a Modificar
- `src/CATRA.App/CATRA.App.csproj` — copiar DLL nativa para output
- `src/CATRA.App/App.xaml.cs` — registrar INativeBridge

### Arquivos NÃO tocar
- `src/CATRA.UI/` — sem UI
- `src/CATRA.Services/Library/` — sem scanner

## Requisitos Técnicos

### catra_gpu.h (API C flat)
- Exatamente a API da spec (seção Native Bridge):
  - `catra_init`, `catra_shutdown`
  - `catra_get_upscale_mode`, `catra_is_fsr4_available`, `catra_get_interp_method`
  - `catra_interp_create/process/destroy`
  - `catra_upscale_create/process/destroy`
  - `catra_encode_create/frame/flush/destroy`
- Todos retornam `int` (0 = success, negativo = erro)
- `__declspec(dllexport)` + `extern "C"`
- Error codes: `#define CATRA_OK 0`, `CATRA_ERR_INIT -1`, `CATRA_ERR_NOT_IMPL -2`, etc.

### catra_gpu.cpp (Skeleton)
- `catra_init`: criar D3D12 device, alocar command queue → retornar OK
- `catra_shutdown`: liberar recursos
- `catra_is_fsr4_available`: retornar 0 (não implementado)
- `catra_get_upscale_mode`: retornar 0 (off)
- Demais funções: retornar `CATRA_ERR_NOT_IMPL`
- Logging: `catra_set_log_callback(void (*cb)(const char* msg, int level))`

### d3d_interop (Stub)
- `CreateD3D12Device()` — wrapper para `D3D12CreateDevice`
- `ShareTexture(ID3D11Texture2D*)` → `ID3D12Resource*` (shared handle + keyed mutex)
- Skeleton: compila mas funções retornam not-implemented

### CMakeLists.txt
- `cmake_minimum_required(VERSION 3.25)`
- `project(catra-gpu LANGUAGES CXX)`
- C++20, MSVC
- vcpkg toolchain (via `scripts/build-native.ps1`)
- Output: `catra-gpu.dll` + `catra-gpu.lib`
- Link: `d3d12.lib`, `dxgi.lib`, `d3d11.lib`
- Install: copiar para `src/CATRA.App/bin/{config}/runtimes/win-x64/native/`

### NativeBridge.cs (P/Invoke)
```csharp
public interface INativeBridge : IDisposable
{
    void Initialize(IntPtr d3d11Device);
    void Shutdown();
    bool IsFsr4Available();
    int GetUpscaleMode();
    // Interp, Upscale, Encode: IntPtr contexts (stub por agora)
}
```
- `DllImport("catra-gpu.dll", CallingConvention = CallingConvention.Cdecl)`
- Log callback: delegate marshaled para C# (captura logs nativos)
- Throw `NativeBridgeException` se retorno < 0
- `IDisposable`: chama `catra_shutdown`

### Build Script
```powershell
# scripts/build-native.ps1
vcpkg install --triplet x64-windows
cmake -B build -S native/catra-gpu -DCMAKE_TOOLCHAIN_FILE=[vcpkg root]/scripts/buildsystems/vcpkg.cmake
cmake --build build --config Release
# copiar DLL para output
```

## Critérios de Sucesso
- [ ] `cmake --build` compila catra-gpu.dll sem erros
- [ ] `dotnet build` passa (copia DLL nativa para output)
- [ ] `dotnet test` passa: NativeBridge.Initialize() + Shutdown() sem crash
- [ ] `IsFsr4Available()` retorna false (stub)
- [ ] Stubs retornam CATRA_ERR_NOT_IMPL (testado)
- [ ] Log callback captura mensagens nativas no C#
- [ ] `scripts/build-native.ps1` executa end-to-end

## Dependências
- ST-01 (estrutura do repo)

## Notas
- vcpkg precisa estar instalado: `git clone https://github.com/microsoft/vcpkg && vcpkg bootstrap-vcpkg.bat`
- Visual Studio 2022 com "Desktop development with C++" workload
- Windows SDK 10.0.22621+ para headers D3D12
- Esta subtask é pré-requisito para ST-13, ST-14, ST-15, ST-16
- Não tentar integrar FSR/RIFE/AMF ainda — só a plumbing

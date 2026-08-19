# CATRA — AI Agent Instructions

CATRA is a C# WPF media player with a native C++20 GPU bridge (`native/catra-gpu`) for frame interpolation, upscaling, frame generation, and hardware encoding.

## Project Structure

```
CATRA.sln                          # Main .NET solution
src/
  CATRA.App/       # WPF application (HwndHost video, playback controls)
  CATRA.Core/      # Core services, interfaces, domain models
  CATRA.Data/      # Data access, file I/O, queue management
  CATRA.Services/  # GPU bridge P/Invoke, FFmpeg interop, pipeline orchestration
  CATRA.UI/        # WPF controls, styles, views
native/
  catra-gpu/       # C++20 native bridge (C ABI for P/Invoke)
    CMakeLists.txt # CMake build (vcpkg + DirectX + optional ONNX/PyTorch/AMF)
    catra_gpu.h    # Flat C ABI surface (all P/Invoke signatures)
    catra_gpu.cpp  # Lifecycle, registries, GuardCabi exception barrier
    d3d_interop.*  # D3D11↔DX12 NT shared handles + keyed mutex pool
    interp_rife.*  # RIFE v4 frame interpolation (ONNX Runtime + DirectML/ROCm)
    upscale_fsr1.* # FSR 1 EASU (self-contained, always compiled)
    upscale_fsr4.* # FSR 4/3.1 via FidelityFX SDK runtime-loaded
    encode_amf.*   # AMF H.265 HEVC encoder (optional, CATRA_HAS_AMF)
    catra_fg.*     # FSR 3 Frame Generation playback (HwndHost)
    ffx_runtime.*  # FFX API 2.x runtime loader (LoadLibrary + GetProcAddress)
    nv12_to_bgra_shader.* # NV12→BGRA conversion shader
    tools/         # Standalone smoke tests (fg_smoke_test, interop_readback_test, ffx_load_smoke_test)
docs/              # Troubleshooting, architecture notes, FSR/AMF issues
scripts/           # Build scripts (build-native.ps1, build-native-vs.bat)
lib/               # Vendored FidelityFX SDK 2.3.0 headers + prebuilt FFX DLLs (gitignored)
```

## Build Commands

```powershell
# Full build (from repo root)
.\build_native_now.bat

# Native only (CMake + vcpkg)
.\scripts\build-native.ps1

# Visual Studio (Ctrl+B triggers BeforeBuild → native build)
# Opens CATRA.sln, then Ctrl+Shift+B
```

The native bridge builds via CMake. The .NET project has a `BeforeBuild` target that runs `build-native-vs.bat` automatically.

## Key Architecture Concepts

### Native Bridge (catra-gpu.dll)
- **Flat C ABI** (`extern "C"`, POD args, `int` return codes) for P/Invoke from C#
- **Exception barrier**: Every entry point wrapped in `GuardCabi`/`GuardCabiVoid` — no C++ exception escapes to managed code
- **Context handles**: Dense non-negative `int` handles, echoed back in every call
- **Resource ownership**: Textures handed back to caller must be released via `catra_release_texture()`; frame arrays via `catra_free()`

### GPU Pipeline
```
FFmpeg D3D11VA decode (NV12)
  → NV12→BGRA conversion (D3D11 compute shader)
  → RIFE interpolation (D3D11, ONNX Runtime)
  → FSR upscale (D3D12, FidelityFX SDK)
  → AMF encode (D3D12, optional)
```

### D3D11↔DX12 Interop (`d3d_interop.cpp`)
- NT shared handles + keyed mutex for zero-copy texture sharing
- **Pooled GPU-GPU copy**: Persistent pool of N shared textures, round-robin, rebuilt only on resolution change
- **Keyed mutex ping-pong**: Keys alternate 0/1 per frame — critical for deadlock-free synchronization
- **Flush guarantee**: D3D11 `Flush()` MUST happen between `CopyResource` and `ReleaseSync()` to prevent GPU hangs

### FidelityFX Runtime Loading (`ffx_runtime.cpp`)
- **NO build-time dependency** on FFX SDK — all DLLs loaded via `LoadLibrary` + `GetProcAddress`
- 8 runtime DLLs must sit next to `catra-gpu.dll` in `runtimes/win-x64/native/`
- FFX runtime auto-selects FSR 4 ML (RDNA 4) or FSR 3.1 fallback
- Availability probed at runtime, not compile time

### RIFE Multi-Backend (`interp_rife.cpp`)
- Priority: ROCm (PyTorch) → DirectML → CPU
- ONNX Runtime with DirectML EP (AMD RDNA 4) or CPU EP fallback
- ROCm requires libtorch standalone with ROCm support

## Critical Conventions & Gotchas

### Thread Safety
- **Backend `Process()` methods are NOT thread-safe** — each upscaler/interpolator instance has internal command allocators shared across calls
- Async workers MUST acquire `processMutex` before calling `Process()`
- Global interop pool (`g_pool`, `g_poolIndex`, `g_frameKey`) is protected by `g_mutex` — concurrent access from multiple video contexts causes keyed mutex key desync

### Resource Lifecycle
- `catra_init()` creates shared D3D12 device + DIRECT queue on same adapter as D3D11
- `catra_shutdown()` tears down in order: FG contexts → encoders → interpolators → upscalers → interop → D3D12 → D3D11
- Caller-owned textures returned by `catra_upscale_process()` and `catra_interp_process()` MUST be released by caller

### Keyed Mutex Protocol
- Producer: `AcquireSync(k, 5000) → CopyResource → Flush → ReleaseSync(k); k ^= 1`
- Consumer: `AcquireSync(k, 5000) → use → ReleaseSync(k); k ^= 1`
- Pool rebuild resets producer key to 0 — consumers must detect generation change and reset their key

### FFX DLL Deployment
- CMake `CMAKE_INSTALL_PREFIX` defaults to `<repo>/install` (not Program Files)
- POST_BUILD copies 8 FFX DLLs next to test executables
- `FfxRuntime::Load()` uses explicit full path from module directory (works even when CWD ≠ exe dir)

### AMF Encoder
- Optional backend (`CATRA_HAS_AMF`), degrades to `CATRA_ERR_NOT_IMPL` when headers absent
- Input: D3D12 NV12 texture (zero-copy via `CreateSurfaceFromDX12Native`)
- Output: context-owned staging buffer (valid until next encode/flush on same context)
- Keyed mutex guard: encoder QIs mutex from input texture, acquires before submit, releases after

## Documentation

- **Architecture specs**: `.agents/specs/` — integration plans, story definitions
- **Sprint planning**: `.agents/sprint_atual/` — current sprint tasks and execution order
- **Architecture decisions**: `.agents/decisions/` — ADRs for major choices
- **Troubleshooting**: `docs/` — FSR/AMF issues, RIFE backends, diagnostic logs
- **Testing conventions**: `.agents/project/testing-conventions.md`

## Common Pitfalls

1. **Black output from FSR1**: Usually a resource state issue — dstRes created in `UNORDERED_ACCESS` state but barrier transitions missing before dispatch
2. **GPU hang (0x887A0001)**: Missing `Flush()` before `ReleaseSync()` on keyed mutex, or pool generation mismatch
3. **AccessViolationException in async upscale**: Race condition — worker threads calling `Process()` without mutex
4. **FFX runtime unavailable**: 8 DLLs not deployed next to `catra-gpu.dll`, or no DX12 adapter
5. **AMF encode failure**: FSR1 outputs D3D12 texture with `ALLOW_UNORDERED_ACCESS` flag — AMF needs `COMMON` state (barrier transition required)
6. **PyTorch detection**: `pip install` PyTorch does NOT include C++ API headers — need standalone libtorch from pytorch.org

## Language/Stack

- **C# (.NET 8)**: WPF application, P/Invoke, FFmpeg.AutoGen, Avalonia (if used)
- **C++20**: Native bridge, DirectX 11/12, FidelityFX SDK, ONNX Runtime, AMF
- **CMake 3.25+**: Native build with vcpkg
- **PowerShell**: Build scripts, installation automation

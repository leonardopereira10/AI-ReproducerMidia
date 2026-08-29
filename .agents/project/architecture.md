# CATRA — Architecture

## Project Structure
```
CATRA.sln
src/
  CATRA.App/       # WPF application entry point (App.xaml, MainWindow.xaml)
  CATRA.Core/      # Domain layer: interfaces, models, enums, processing contracts
  CATRA.Data/      # SQLite persistence: entities, repositories, database init
  CATRA.Services/  # Implementation layer: playback, casting, library, processing, web control
  CATRA.UI/        # WPF presentation: views, viewmodels, controls, themes, navigation
tests/
  CATRA.Core.Tests/
  CATRA.Data.Tests/
  CATRA.Services.Tests/
  CATRA.UI.Tests/
native/
  catra-gpu/       # C++20 native bridge (C ABI for P/Invoke)
    catra_gpu.h/cpp    # Flat C ABI surface, lifecycle, GuardCabi exception barrier
    d3d_interop.h/cpp  # D3D11↔DX12 NT shared handles + keyed mutex pool (2393 lines)
    interp_rife.h/cpp  # RIFE v4 frame interpolation (ONNX Runtime + DirectML/ROCm)
    upscale_fsr1.h/cpp # FSR 1 EASU compute shader (self-contained, always compiled)
    upscale_fsr4.h/cpp # FSR 4/3.1 via FidelityFX SDK runtime-loaded (FFX API 2.x)
    encode_amf.h/cpp   # AMF H.265 HEVC encoder (optional, CATRA_HAS_AMF)
    catra_fg.h/cpp     # FSR 3 Frame Generation playback (HwndHost, DX12 swapchain)
    ffx_runtime.h/cpp  # FFX API 2.x runtime loader (LoadLibrary + GetProcAddress)
    nv12_to_bgra_shader.h/cpp # NV12→BGRA D3D11 compute shader
    tools/             # Smoke tests (fg, interop readback, ffx load)
docs/              # Technical documentation
scripts/           # Build/install scripts (PowerShell)
lib/               # Vendored SDKs (FidelityFX 2.3.0, AMF, FFmpeg, ONNX Runtime, RIFE models)
```

## Implemented Features (Current State)

### ✅ Completed
- **WPF App Shell** — MainWindow, navigation (Home → Detail → Player → Queue → Settings)
- **SQLite Database** — 7 entities (MediaItem, Episode, Category, ProcessJob, ProcessedFile, WatchState, AppSetting)
- **Library Scanner** — Recursive folder scan, filename parser (anime/series patterns), media type detection
- **Library Watcher** — FileSystemWatcher for real-time library updates
- **FFmpeg Playback** — D3D11VA decode (NV12), BGRA render via HwndHost SwapChain
- **Player Controls** — Custom transport controls, seek bar, volume, episode navigation
- **Watch State** — Position tracking, resume playback, completion threshold
- **DLNA Casting** — SSDP discovery, AVTransport/RenderingControl SOAP, media HTTP server
- **Thumbnails** — FFmpeg frame extraction, thumbnail service
- **Theme System** — Windows light/dark theme following, ResourceDictionary-based
- **Settings UI** — App settings with profile selection
- **Native Bridge** — Full C ABI surface (420 lines header), context handles, exception barrier
- **RIFE Interpolation** — Multi-backend (ROCm → DirectML → CPU), ONNX Runtime
- **FSR 1 Upscale** — EASU compute shader (D3D11, always available)
- **FSR 4/3.1 Upscale** — FFX API 2.x runtime-loaded, video mode (zero-MV), BGRA output
- **D3D11↔DX12 Interop** — NT shared handles, keyed mutex pool, GPU-GPU zero-copy
- **AMF Encode** — H.265 HEVC hardware encoder (optional, D3D12 input)
- **Frame Generation** — FSR 3 FG via FFX swapchain (DX12, optical flow internal)
- **Processing Pipeline** — Orchestration C# (decode → interp → upscale → encode)
- **Sliding Window** — Pre-processing window with rotation, queue management
- **Queue Resume** — Skip completed jobs with valid ProcessedFile, orphan recovery on restart
- **Web Control Panel** — HTTP server (Kestrel :5050), WebSocket, mobile-first frontend
- **Cleanup Service** — Processed file cleanup on close and startup

### 🔲 Planned (Stubs)
- **Online Metadata** — TMDB/AniList integration (ST-23)
- **Subtitles** — MKV embedded + .srt overlay (ST-24)
- **Search/Filter** — Library search and filtering (ST-25)
- **Auto-Reprocess** — Hash-based reprocess on source change (ST-26)
- **Bluetooth Remote** — WinRT Bluetooth control (ST-27)
- **Multi-Profile** — Multiple user profiles (ST-28)

## GPU Pipeline
```
FFmpeg D3D11VA decode (NV12)
  → NV12→BGRA conversion (D3D11 compute shader)
  → RIFE interpolation (D3D11, ONNX Runtime)
  → FSR upscale (D3D12, FidelityFX SDK — FSR 4 ML or FSR 3.1 fallback)
  → AMF encode (D3D12, optional)
```

### Playback Path (Frame Generation)
```
FFmpeg D3D11VA decode (NV12)
  → NV12→BGRA conversion (D3D11)
  → D3D11↔D3D12 interop (NT shared handle + keyed mutex)
  → FSR 3 FG Swapchain (DX12, optical flow, HwndHost present)
```

## Key Patterns
- **Native Bridge:** Flat C ABI (`extern "C"`, POD args, `int` return codes) for P/Invoke
- **Exception Barrier:** Every native entry point wrapped in `GuardCabi`/`GuardCabiVoid`
- **Context Handles:** Dense non-negative `int` handles, echoed back in every call
- **D3D11↔DX12 Interop:** NT shared handles + keyed mutex for zero-copy texture sharing
- **Keyed Mutex Protocol:** Producer/consumer ping-pong with alternating keys (0/1)
- **FFX Runtime Loading:** No build-time SDK dependency — LoadLibrary + GetProcAddress
- **MVVM:** CommunityToolkit.Mvvm with source generators
- **DI:** Microsoft.Extensions.DependencyInjection

## Agent Configuration
- **Agents:** `.agents/agents/` — 10 agents (dev×4 + qa×4 + engineer + PO)
- **Skills:** `.agents/skills/` — project-specific skills
- **Specs:** `.agents/specs/` — feature specs and sprint plans
- **Sprint State:** `.agents/sprint_atual/` — active sprint subtasks
- **Learning:** `.agents/Learning/` — sprint history and retrospectives
- **Decisions:** `.agents/decisions/` — ADRs (template available, no ADRs yet)

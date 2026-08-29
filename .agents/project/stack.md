# CATRA — Tech Stack

## Runtime & Framework
- **Runtime:** .NET 8+
- **Language:** C# 12
- **UI:** WPF (Windows Presentation Foundation)
- **Architecture:** MVVM (CommunityToolkit.Mvvm)

## Key Libraries
- **Media:** FFmpeg.AutoGen, Vortice.Windows (DirectX)
- **Database:** SQLite (sqlite-net-pcl)
- **MVVM:** CommunityToolkit.Mvvm
- **Testing:** xUnit, FluentAssertions
- **Native Bridge:** C++20, CMake 3.25+, vcpkg
- **Web Control:** ASP.NET Core Kestrel (embedded HTTP + WebSocket)

## Native Components
- **GPU Bridge:** catra-gpu.dll (C++20, C ABI for P/Invoke)
- **Frame Interpolation:** RIFE v4 (ONNX Runtime + DirectML/ROCm)
- **Upscaling:** FSR 1 EASU (D3D11 compute) + FSR 4/3.1 (FidelityFX SDK, runtime-loaded)
- **Encoding:** AMF H.265 HEVC (optional, D3D12)
- **Frame Generation:** FSR 3 FG (FidelityFX SDK, DX12 swapchain, optical flow)
- **Interop:** D3D11↔DX12 via NT shared handles + keyed mutex pool

## Build Tools
- **Solution:** CATRA.sln
- **Native Build:** CMake + vcpkg
- **Scripts:** `scripts/build-native.ps1`, `build_native_now.bat`

## Vendored SDKs (lib/)
- **FidelityFX SDK 2.3.0** — FFX API headers (MIT, vendored) + 8 runtime DLLs (prebuilt samples)
- **AMF** — AMD Media Framework headers (optional, for H.265 encode)
- **FFmpeg** — Prebuilt binaries for FFmpeg.AutoGen
- **ONNX Runtime** — Inference engine for RIFE
- **RIFE models** — ONNX model files

## Hardware Target
- **GPU:** AMD Radeon RX 9070 XT (RDNA 4)
- **Features:** ML accelerators, AMF encode, FSR 4 ML upscale

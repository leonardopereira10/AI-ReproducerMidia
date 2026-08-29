# CATRA — Build Commands

## .NET Build
```bash
dotnet build CATRA.sln --no-restore
```

## .NET Build + Test
```bash
dotnet build CATRA.sln --no-restore
dotnet test --no-build
```

## Native Bridge Build
```bash
powershell -File scripts/build-native.ps1
```

## Full Build (native + .NET)
```bash
.\build_native_now.bat
```

## Run Application
```bash
dotnet run --project src/CATRA.App
```

## Clean
```bash
dotnet clean
```

## Native Smoke Tests
```bash
# After native build, smoke tests are in native/catra-gpu/build/
native/catra-gpu/build/Debug/fg_smoke_test.exe
native/catra-gpu/build/Debug/interop_readback_test.exe
native/catra-gpu/build/Debug/ffx_load_smoke_test.exe
```

## Notes
- Native bridge must be built before .NET build for native-dependent features
- The .NET project has a `BeforeBuild` target that runs `build-native-vs.bat` automatically when building from Visual Studio
- FFX runtime DLLs (8 files) must be deployed next to `catra-gpu.dll` in `runtimes/win-x64/native/`
- CMake `CMAKE_INSTALL_PREFIX` defaults to `<repo>/native/catra-gpu/install` (not Program Files)
- AMF encoder is optional — compiled only when `CATRA_HAS_AMF` is defined (requires AMF headers via `-DCATRA_AMF_ROOT`)
- RIFE models are in `lib/rife/` — ONNX format, loaded at runtime by `interp_rife.cpp`

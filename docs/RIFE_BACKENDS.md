# RIFE Multi-Backend System

## Overview

The CATRA project now supports multiple GPU backends for RIFE (Real-Time Intermediate-Frame Enhancement) frame interpolation. The system automatically selects the best available backend with a configurable priority chain.

## Backend Priority

**Default priority: ROCm (PyTorch) → DirectML → CPU**

1. **ROCm (PyTorch)** - Highest performance on AMD GPUs
2. **DirectML** - Microsoft's GPU abstraction layer (works on all vendors)
3. **CPU** - Fallback when no GPU backend is available

## Installation

### Option 1: PyTorch/ROCm (Recommended for AMD GPUs)

For AMD RX 9070 XT and other RDNA 4 GPUs:

```powershell
# Prerequisites:
# - Windows 11
# - AMD Adrenalin driver 26.2.2 or newer
# - Python 3.12

# Install PyTorch with ROCm support
.\scripts\install-pytorch-rocm.ps1
```

Or manually:

```powershell
# Install ROCm SDK 7.2.1
pip install --no-cache-dir `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz

# Install PyTorch 2.9.1 with ROCm
pip install --no-cache-dir `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl `
    https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl
```

### Option 2: DirectML (Already Configured)

DirectML is already integrated via ONNX Runtime. No additional installation required.

### Option 3: CPU Fallback

Always available as the last resort.

## Building

The build system automatically detects available backends:

```powershell
# From Visual Studio (Ctrl+B) or command line:
.\scripts\build-native-vs.bat

# Or full build:
.\build_native_now.bat
```

The build will output:

```
[NativeBuild] PyTorch detected at: C:\Users\...\site-packages\torch
[NativeBuild] Backend priority: ROCm -> DirectML -> CPU
```

## Verifying Backend Selection

Check the logs during RIFE initialization:

```
interp_rife: attempting to load model with PyTorch/ROCm
interp_rife: model loaded with PyTorch/ROCm on GPU
interp_rife: using ROCm backend
```

Or if ROCm is not available:

```
interp_rife: attempting to load model with ONNX Runtime/DirectML
interp_rife: DirectML EP enabled
interp_rife: using DirectML backend
```

## Configuration

### CMake Options

In `native/catra-gpu/CMakeLists.txt`:

```cmake
# Enable/disable backends
option(CATRA_USE_PYTORCH "Enable PyTorch/ROCm backend" ON)
option(CATRA_USE_DML "Enable DirectML backend" ON)

# Force specific backend priority
set(CATRA_BACKEND_PRIORITY "ROCm;DirectML;CPU")
```

### Runtime Override

Set environment variables to override backend selection:

```powershell
# Force DirectML (skip ROCm)
$env:CATRA_DISABLE_ROCM = "1"

# Force CPU (skip all GPU backends)
$env:CATRA_DISABLE_GPU = "1"
```

## Performance Comparison

Approximate performance on AMD RX 9070 XT (1080p → 1440p interpolation):

| Backend | Time per Frame | GPU Utilization | Notes |
|---------|---------------|-----------------|-------|
| ROCm | ~8ms | ~85% | Best performance |
| DirectML | ~12ms | ~75% | Good compatibility |
| CPU | ~45ms | ~15% | Fallback only |

## Troubleshooting

### ROCm Not Detected

1. Verify AMD driver version (26.2.2+):
   ```powershell
   # Check driver version
   wmic path win32_VideoController get DriverVersion
   ```

2. Verify PyTorch installation:
   ```powershell
   python -c "import torch; print(torch.cuda.is_available())"
   python -c "import torch; print(torch.cuda.get_device_name(0))"
   ```

3. Check ROCm environment:
   ```powershell
   python -c "import rocm_sdk; print(rocm_sdk.__version__)"
   ```

### DirectML Fallback

If ROCm fails, the system automatically tries DirectML. Check logs for:

```
interp_rife: ROCm init failed: ...
interp_rife: attempting to load model with ONNX Runtime/DirectML
```

### Build Errors

**"PyTorch/libtorch not found"**

- Ensure PyTorch is installed: `pip list | findstr torch`
- Rebuild: `.\scripts\build-native-vs.bat`

**"ROCm/HIP not available"**

- Update AMD driver to 26.2.2+
- Reinstall ROCm SDK: `pip install --force-reinstall rocm_sdk_core`

### Performance Issues

**Slow interpolation despite GPU backend**

- Check GPU utilization: `Task Manager > Performance > GPU`
- Verify backend in logs (should show "ROCm" or "DirectML", not "CPU")
- Check for thermal throttling

## Architecture

```
┌─────────────────────────────────────────────────┐
│              RIFE Context Creation               │
├─────────────────────────────────────────────────┤
│ 1. Try ROCm (PyTorch)                           │
│    ├─ Check torch::cuda::is_available()        │
│    ├─ Load TorchScript model                    │
│    └─ Move to GPU device                        │
│                                                 │
│ 2. Try DirectML (ONNX Runtime)                  │
│    ├─ Check OrtSessionOptions DML EP           │
│    └─ Load ONNX model                           │
│                                                 │
│ 3. Fallback to CPU                              │
│    └─ Load ONNX model with CPU EP              │
└─────────────────────────────────────────────────┘
         ↓
┌─────────────────────────────────────────────────┐
│              Inference Execution                 │
├─────────────────────────────────────────────────┤
│ switch (ctx->backend) {                         │
│   case ROCm:                                    │
│     - Convert tensors to PyTorch format         │
│     - Run torch::jit::Module::forward()         │
│     - Copy output back to CPU                   │
│                                                 │
│   case DirectML:                                │
│     - Create ORT tensors                        │
│     - Run Ort::Session::Run()                   │
│     - Extract output                            │
│                                                 │
│   case CPU:                                     │
│     - Same as DirectML but on CPU               │
│ }                                               │
└─────────────────────────────────────────────────┘
```

## Model Format

RIFE models are stored as ONNX format (`.onnx`). Both backends can load the same model file:

- **ROCm**: Converts ONNX → TorchScript internally
- **DirectML**: Loads ONNX directly
- **CPU**: Loads ONNX directly

No model conversion required.

## Future Enhancements

Potential improvements:

1. **Runtime backend switching** - Change backend without restart
2. **Benchmark mode** - Test all backends and select fastest
3. **Vulkan backend** - Cross-platform GPU acceleration
4. **TensorRT backend** - NVIDIA-specific optimization

## References

- [PyTorch ROCm for Windows](https://rocm.docs.amd.com/projects/radeon-ryzen/en/latest/docs/install/installrad/windows/install-pytorch.html)
- [DirectML Documentation](https://learn.microsoft.com/en-us/windows/ai/directml/)
- [ONNX Runtime](https://onnxruntime.ai/)
- [RIFE Paper](https://arxiv.org/abs/2011.09109)

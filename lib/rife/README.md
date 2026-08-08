# RIFE v4 ONNX Model

This directory must contain `rife_v4.onnx` for the native RIFE interpolation
backend (interp_rife.cpp) to work. The file is **not committed** (gitignored)
and there is **no official ONNX release** — it must be exported manually.

## Quick export (tested with RIFE v4.25)

```bash
# 1. Clone Practical-RIFE
git clone --depth 1 https://github.com/hzwer/Practical-RIFE
cd Practical-RIFE

# 2. Download v4.25 weights (recommended) and extract into train_log/
#    Google Drive: https://drive.google.com/file/d/1ZKjcbmt1hypiFprJPIKW0Tt0lr_2i7bg
pip install gdown
gdown 1ZKjcbmt1hypiFprJPIKW0Tt0lr_2i7bg -O rife425.zip
unzip -o rife425.zip   # creates train_log/ with RIFE_HDv3.py, IFNet_HDv3.py, flownet.pkl

# 3. Install deps
pip install torch onnx onnxruntime onnxscript numpy

# 4. Copy the export script from this directory and run it
cp <CATRA_REPO>/lib/rife/export_onnx.py .
python export_onnx.py -o rife_v4.onnx

# 5. Copy the model here
cp rife_v4.onnx <CATRA_REPO>/lib/rife/rife_v4.onnx
```

The export script (`export_onnx.py` in this directory) handles:
- Patching the `warp()` function for ONNX compatibility (removes global cache)
- Padding inputs to multiples of 128 (required by the stride-2 conv cascade)
- Dynamic axes for batch/height/width
- Validation with onnxruntime at multiple resolutions

## Alternative: set env var

```powershell
$env:CATRA_RIFE_MODEL_PATH = "C:\path\to\rife_v4.onnx"
```

The native bridge checks this env var first, then falls back to `lib/rife/rife_v4.onnx`
relative to the DLL location.

## ONNX I/O contract

| Direction | Name       | Shape                    | Type   |
|-----------|------------|--------------------------|--------|
| input     | `img0`     | [batch, 3, height, width] | float32 |
| input     | `img1`     | [batch, 3, height, width] | float32 |
| input     | `timestep` | [1]                       | float32 |
| output    | `output`   | [batch, 3, height, width] | float32 |

## Notes

- The model is ~22 MB (RIFE v4.25).
- **Tensor names are flexible**: interp_rife.cpp queries I/O names from the
  ONNX graph at runtime (does NOT hardcode them). The timestep input is detected
  by name (`timestep` / `time` / `t`, case-insensitive) or falls back to index 2.
- Use `opset_version=17` to match ORT 1.17/1.18.
- DirectML EP is used when available (GPU acceleration); otherwise CPU fallback.
  The DML EP is loaded via `GetProcAddress` — no link-time dependency.
- The ORT C API version is negotiated at runtime (`ORT_API_MANUAL_INIT`), so
  header/DLL version mismatches in the NuGet package are handled gracefully.

# RIFE v4 ONNX Model

This directory must contain `rife_v4.onnx` for the native RIFE interpolation
backend (interp_rife.cpp) to work. The file is **not committed** (gitignored)
and there is **no official ONNX release** — it must be exported manually.

## How to export

```bash
# 1. Clone Practical-RIFE
git clone https://github.com/hzwer/Practical-RIFE
cd Practical-RIFE

# 2. Download pretrained weights (rife-v4.x — pick the latest v4 variant)
#    See the repo README for download links (usually Baidu/GDrive).
#    Place them in train_log/ or specify --model_dir.

# 3. Install deps
pip install torch onnx onnxruntime

# 4. Export to ONNX (arbitrary-timestep, 720p reference)
python - <<'EOF'
import torch
from model.RIFE_HDv3 import Model

model = Model()
model.load_model("train_log")  # adjust path to downloaded weights
model.eval()

# Dummy input: two 720p frames + timestep
img0 = torch.randn(1, 3, 720, 1280)
img1 = torch.randn(1, 3, 720, 1280)
timestep = torch.tensor([0.5])

torch.onnx.export(
    model.flownet,
    (img0, img1, timestep),
    "rife_v4.onnx",
    input_names=["img0", "img1", "timestep"],
    output_names=["flow"],
    dynamic_axes={
        "img0": {0: "B", 2: "H", 3: "W"},
        "img1": {0: "B", 2: "H", 3: "W"},
        "flow": {0: "B", 2: "H", 3: "W"},
    },
    opset_version=17,
)
print("Exported rife_v4.onnx")
EOF

# 5. Copy here
cp rife_v4.onnx <CATRA_REPO>/lib/rife/rife_v4.onnx
```

## Alternative: set env var

```powershell
$env:CATRA_RIFE_MODEL_PATH = "C:\path\to\rife_v4.onnx"
```

The native bridge checks this env var first, then falls back to `lib/rife/rife_v4.onnx`
relative to the DLL location.

## Notes

- The model is ~10-20 MB depending on the RIFE v4 variant.
- interp_rife.cpp expects input/output tensor names to match the export.
  If using a different export script, verify the I/O names in the ONNX graph.
- DirectML EP is used when available (GPU acceleration); otherwise CPU fallback.

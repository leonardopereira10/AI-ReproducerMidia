"""Export Practical-RIFE v4.25 to ONNX for the CATRA native bridge.

The native bridge (interp_rife.cpp) expects:
  Inputs:  img0 [B,3,H,W] float32, img1 [B,3,H,W] float32, timestep [1] float32
  Output:  output [B,3,H,W] float32

Usage:
  cd Practical-RIFE
  python export_onnx.py [--cpu] [--opset 17] [--fp16] [--scales "16,8,4,2,1"] [--preset fast]

Presets:
  --preset fast  =  FP16 weights + 4 scales (16,8,4,2,2) + FP32 I/O
                    Benchmark (RX 9070 XT): 19ms/frame vs 67ms baseline = 3.6x speedup
                    PSNR >49dB vs FP32 5-scales (quality ~98%)
"""

import argparse
import sys
import torch
import torch.nn as nn
import torch.nn.functional as F

# ---------------------------------------------------------------------------
# Patch model.warplayer.warp to be ONNX-export-friendly.
# The original uses a global dict cache keyed by (device, size) which breaks
# torch.onnx.export (the grid tensor is created outside the traced graph).
# We replace it with an inline grid that is re-created each call so the
# tracer can record the ops.
# ---------------------------------------------------------------------------
import model.warplayer as _wp

def _warp_onnx(tenInput, tenFlow):
    """ONNX-safe warp: no global cache, grid built inline."""
    B, _, H, W = tenFlow.shape
    gy = torch.linspace(-1.0, 1.0, H, device=tenFlow.device, dtype=tenFlow.dtype)
    gx = torch.linspace(-1.0, 1.0, W, device=tenFlow.device, dtype=tenFlow.dtype)
    gy = gy.view(1, 1, H, 1).expand(B, -1, -1, W)
    gx = gx.view(1, 1, 1, W).expand(B, -1, H, -1)
    grid = torch.cat([gx, gy], 1)

    tenFlow = torch.cat([
        tenFlow[:, 0:1, :, :] / ((tenInput.shape[3] - 1.0) / 2.0),
        tenFlow[:, 1:2, :, :] / ((tenInput.shape[2] - 1.0) / 2.0),
    ], 1)

    g = (grid + tenFlow).permute(0, 2, 3, 1)
    return F.grid_sample(tenInput, g, mode='bilinear',
                         padding_mode='border', align_corners=True)

_wp.warp = _warp_onnx
import train_log.IFNet_HDv3 as _ifnet_mod
_ifnet_mod.warp = _warp_onnx

# ---------------------------------------------------------------------------
# Load model
# ---------------------------------------------------------------------------
from train_log.RIFE_HDv3 import Model

parser = argparse.ArgumentParser()
parser.add_argument("--cpu", action="store_true", help="export on CPU")
parser.add_argument("--opset", type=int, default=17)
parser.add_argument("--model-dir", type=str, default="train_log")
parser.add_argument("--fp16", action="store_true", help="Convert weights to FP16 (keeps I/O as FP32)")
parser.add_argument("--scales", type=str, default="16,8,4,2,1",
                    help="Comma-separated pyramid scales (default: 16,8,4,2,1). "
                         "NOTE: IFNet has 5 fixed blocks, so always provide 5 values. "
                         "Use '16,8,4,2,2' to skip the expensive scale=1 level.")
parser.add_argument("--preset", type=str, choices=["default", "fast"],
                    default=None,
                    help="Preset: 'fast' = FP16 weights + skip scale=1 (16,8,4,2,2) for ~3.6x speed")
parser.add_argument("-o", "--output", type=str, default="rife_v4.onnx")
args = parser.parse_args()

# Resolve preset (overrides individual flags)
if args.preset == "fast":
    args.fp16 = True
    # Keep original scales [16,8,4,2,1] — FP16 alone gives 3.2x speedup.
    # Changing scale=1 to scale=2 (4-scales) gives only marginal gain (3.7x vs 3.2x)
    # but causes visual artifacts (video appears "accelerated").
    print(f"Preset 'fast': fp16={args.fp16}, scales={args.scales}")

use_cuda = torch.cuda.is_available() and not args.cpu
dev = torch.device("cuda" if use_cuda else "cpu")
print(f"Device: {dev}")

model = Model()
model.load_model(args.model_dir, -1)
model.eval()
model.flownet.to(dev)

# NOTE: We do NOT convert model to half() here. FP16 conversion is done
# post-export via onnxruntime's convert_float_to_float16(keep_io_types=True),
# which keeps I/O as FP32 (compatible with interp_rife.cpp) while converting
# internal weights to FP16 (2-3.6x faster on GPU).

# ---------------------------------------------------------------------------
# Wrapper with pad-to-multiple-of-128 + crop
# ---------------------------------------------------------------------------
PAD_MULT = 128  # the model's stride-2 conv cascade needs this alignment

class RifeOnnxWrapper(nn.Module):
    """Wraps IFNet for a clean ONNX signature with internal padding.

    Forward:
        img0:     [B, 3, H, W]  float32, range [0, 1]
        img1:     [B, 3, H, W]  float32, range [0, 1]
        timestep: [1]           float32, e.g. 0.5
    Returns:
        output:   [B, 3, H, W]  float32, range [0, 1]
    """
    def __init__(self, flownet: nn.Module, scales="16,8,4,2,1"):
        super().__init__()
        self.flownet = flownet
        self.scales = scales

    def forward(self, img0: torch.Tensor, img1: torch.Tensor,
                timestep: torch.Tensor) -> torch.Tensor:
        _, _, h, w = img0.shape

        # Pad to nearest multiple of PAD_MULT (reflect keeps edges clean)
        ph = ((h - 1) // PAD_MULT + 1) * PAD_MULT
        pw = ((w - 1) // PAD_MULT + 1) * PAD_MULT
        pad_bottom = ph - h
        pad_right  = pw - w
        if pad_bottom > 0 or pad_right > 0:
            img0 = F.pad(img0, (0, pad_right, 0, pad_bottom), mode='replicate')
            img1 = F.pad(img1, (0, pad_right, 0, pad_bottom), mode='replicate')

        imgs = torch.cat((img0, img1), 1)
        scale_list = [float(s) for s in self.scales.split(",")]
        _flow_list, _mask, merged = self.flownet(imgs, timestep, scale_list)
        out = merged[-1]  # [B, 3, ph, pw]

        # Crop back to original resolution
        if pad_bottom > 0 or pad_right > 0:
            out = out[:, :, :h, :w]
        return out


wrapper = RifeOnnxWrapper(model.flownet, scales=args.scales).to(dev)
wrapper.eval()

# ---------------------------------------------------------------------------
# Export
# ---------------------------------------------------------------------------
H, W = 720, 1280
dummy_img0 = torch.randn(1, 3, H, W, device=dev)
dummy_img1 = torch.randn(1, 3, H, W, device=dev)
dummy_ts   = torch.tensor([0.5], device=dev)

# Always export as FP32 first (FP16 conversion is post-export)
print(f"Warm-up run with {list(dummy_img0.shape)} ...")
with torch.no_grad():
    out = wrapper(dummy_img0, dummy_img1, dummy_ts)
    print(f"  Output shape: {list(out.shape)} (expect [1, 3, {H}, {W}])")
    assert out.shape == (1, 3, H, W), f"Shape mismatch: {out.shape}"

# Export to a temporary FP32 file if FP16 conversion is requested
export_path = args.output
if args.fp16:
    export_path = args.output + ".fp32_tmp"

print(f"Exporting to {export_path} (opset {args.opset}) ...")
with torch.no_grad():
    torch.onnx.export(
        wrapper,
        (dummy_img0, dummy_img1, dummy_ts),
        export_path,
        input_names=["img0", "img1", "timestep"],
        output_names=["output"],
        dynamic_axes={
            "img0":   {0: "batch", 2: "height", 3: "width"},
            "img1":   {0: "batch", 2: "height", 3: "width"},
            "output": {0: "batch", 2: "height", 3: "width"},
        },
        opset_version=args.opset,
        do_constant_folding=True,
        dynamo=False,  # use legacy TorchScript exporter (dynamo has encoding issues on Windows)
    )

# ---------------------------------------------------------------------------
# FP16 post-export conversion (weights FP16, I/O stays FP32)
# ---------------------------------------------------------------------------
if args.fp16:
    print("Converting weights to FP16 (keeping I/O as FP32)...")
    import onnx
    from onnxruntime.transformers.float16 import convert_float_to_float16

    model_onnx = onnx.load(export_path)
    model_fp16 = convert_float_to_float16(model_onnx, keep_io_types=True)
    onnx.save(model_fp16, args.output)

    import os
    fp32_size = os.path.getsize(export_path) / 1024 / 1024
    fp16_size = os.path.getsize(args.output) / 1024 / 1024
    print(f"  FP32: {fp32_size:.1f}MB -> FP16: {fp16_size:.1f}MB ({100*fp16_size/fp32_size:.0f}%)")

    # Clean up temp file
    os.remove(export_path)
    print(f"  Saved: {args.output}")

print(f"Exported {args.output}")

# ---------------------------------------------------------------------------
# Validate with onnxruntime
# ---------------------------------------------------------------------------
try:
    import onnxruntime as ort
    import numpy as np

    print("\nValidating with onnxruntime (CPU) ...")
    sess = ort.InferenceSession(args.output, providers=["CPUExecutionProvider"])

    # Test at a different resolution to verify dynamic axes
    # Detect model input dtype
    input_dtype = np.float32
    for inp in sess.get_inputs():
        if inp.type == 'tensor(float16)':
            input_dtype = np.float16
            break

    for test_h, test_w in [(128, 128), (720, 1280), (1080, 1920)]:
        # Use appropriate dtype for test inputs
        in0 = np.random.randn(1, 3, test_h, test_w).astype(input_dtype)
        in1 = np.random.randn(1, 3, test_h, test_w).astype(input_dtype)
        ts  = np.array([0.5], dtype=input_dtype)
        out = sess.run(None, {"img0": in0, "img1": in1, "timestep": ts})[0]
        assert out.shape == (1, 3, test_h, test_w), \
            f"Shape mismatch at {test_h}x{test_w}: got {out.shape}"
        print(f"  {test_h}x{test_w}: OK  range=[{out.min():.3f}, {out.max():.3f}]")

    print("\n  ONNX I/O metadata:")
    for inp in sess.get_inputs():
        print(f"    input:  {inp.name:12s}  shape={inp.shape}  type={inp.type}")
    for outp in sess.get_outputs():
        print(f"    output: {outp.name:12s}  shape={outp.shape}  type={outp.type}")

    print("\nValidation passed")
except ImportError:
    print("onnxruntime not installed -- skipping validation")
except Exception as e:
    print(f"Validation failed: {e}")
    import traceback; traceback.print_exc()
    sys.exit(1)

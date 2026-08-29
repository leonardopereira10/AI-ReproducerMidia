# AMF Encoder × D3D12 Textures — Known Issue & Workaround

## Problem

`AMFContext2::CreateSurfaceFromDX12Native` fails (`AMF_FAIL`, res=1) when consuming
D3D12 textures produced by the FSR upscale pipeline on AMD RDNA 4 (RX 9070 XT).

The FSR 4/3.1 upscale outputs D3D12 textures with `ALLOW_UNORDERED_ACCESS` flag.
AMF expects textures in `COMMON` state and may not handle the `ALLOW_UNORDERED_ACCESS`
flag correctly when importing from D3D12.

## Workaround (Implemented)

**D3D12→D3D11 copy at the encode boundary** (`story_03_contorno_amf_encode_offline`):

Before feeding the upscaled frame to AMF, the texture is copied from D3D12 back to
D3D11 via NT shared handle + keyed mutex (same interop mechanism as the decode path,
but in reverse). The D3D11 copy is then fed to AMF via `CreateSurfaceFromDX11Native`
(which works reliably).

This adds one extra GPU-GPU copy per frame at the encode boundary but ensures
compatibility with AMF's surface import.

## Impact

- **Offline encode path:** One extra D3D12→D3D11 copy per frame (GPU-GPU, ~0.1ms)
- **Playback path (FG):** Not affected — Frame Generation does not use AMF
- **FSR 1 → AMF:** Same workaround applies (FSR1 also outputs D3D12)

## Status

Workaround is implemented and functional. Root cause analysis suggests an AMF driver-level
issue with D3D12 surface import on RDNA 4. May be resolved in future AMD driver updates.

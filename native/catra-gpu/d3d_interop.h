// d3d_interop.h — D3D11 <-> DX12 texture sharing helpers (ST-12 skeleton).
//
// FFmpeg's D3D11VA decoder produces ID3D11Texture2D, while FSR 4 / the DX12
// compute path need ID3D12Resource. These helpers bridge the two via NT shared
// handles + keyed mutex so frames cross without a CPU round-trip.
//
// STATUS (ST-12): declarations + stub bodies only. The real share/import logic
// (CreateSharedHandle / OpenSharedHandle, keyed-mutex acquire/release) lands
// with the DX12 device work in ST-15.

#ifndef CATRA_D3D_INTEROP_H
#define CATRA_D3D_INTEROP_H

#include "catra_gpu.h"

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

namespace catra {

using Microsoft::WRL::ComPtr;

// Creates a DX12 device on the same adapter as `d3d11Device` and returns it.
// Skeleton: returns E_NOTIMPL and leaves `out` empty.
HRESULT CreateD3D12Device(ID3D11Device* d3d11Device,
                          ID3D12Device** out);

// Shares a D3D11 texture into a DX12 resource (NT handle + keyed mutex).
// Skeleton: returns E_NOTIMPL and leaves `out` empty.
HRESULT ShareTexture(ID3D11Device* d3d11Device,
                     ID3D12Device* d3d12Device,
                     ID3D11Texture2D* source,
                     ID3D12Resource** out);

} // namespace catra

#endif // CATRA_D3D_INTEROP_H

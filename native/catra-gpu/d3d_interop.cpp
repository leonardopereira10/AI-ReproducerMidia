// d3d_interop.cpp — D3D11 <-> DX12 sharing helpers (ST-12 skeleton).
//
// Bodies validate arguments and return E_NOTIMPL. The real implementation
// (shared handles + keyed mutex) is delivered with the DX12 device in ST-15.

#include "d3d_interop.h"

namespace catra {

HRESULT CreateD3D12Device(ID3D11Device* d3d11Device, ID3D12Device** out)
{
    if (out == nullptr)
    {
        return E_INVALIDARG;
    }
    *out = nullptr;

    if (d3d11Device == nullptr)
    {
        return E_INVALIDARG;
    }

    // ST-15: query the adapter via IDXGIDevice -> IDXGIAdapter, then
    // D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_12_0, ...).
    return E_NOTIMPL;
}

HRESULT ShareTexture(ID3D11Device* d3d11Device,
                     ID3D12Device* d3d12Device,
                     ID3D11Texture2D* source,
                     ID3D12Resource** out)
{
    if (out == nullptr)
    {
        return E_INVALIDARG;
    }
    *out = nullptr;

    if (d3d11Device == nullptr || d3d12Device == nullptr || source == nullptr)
    {
        return E_INVALIDARG;
    }

    // ST-15: create the D3D11 texture with D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX
    // + SHARED_NTHANDLE, CreateSharedHandle, then
    // ID3D12Device::OpenSharedHandle on the other side.
    return E_NOTIMPL;
}

} // namespace catra

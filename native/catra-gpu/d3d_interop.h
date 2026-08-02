// d3d_interop.h — D3D11 <-> DX12 texture sharing (ST-15).
//
// FFmpeg's D3D11VA decoder produces ID3D11Texture2D, while FSR 4 / the DX12
// compute path need ID3D12Resource. These helpers bridge the two via NT shared
// handles + keyed mutex so frames cross without a CPU round-trip.
//
// LAYOUT
//   * interop_init / interop_shutdown own the bridge's D3D12 device, DIRECT
//     command queue, command allocator/list and fence (created on the SAME
//     adapter as the caller's D3D11 device, which is a hard requirement for
//     shared-handle interop). catra_gpu.cpp drives both from catra_init /
//     catra_shutdown.
//   * interop_share_d3d11_to_d3d12 has two paths:
//       - ZERO-COPY: the source already carries
//         D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX | _SHARED_NTHANDLE (some
//         FFmpeg D3D11VA pools do). An NT handle is minted on the source and
//         opened on the D3D12 device; no copy, no pool involvement.
//       - POOLED COPY: the source is not NT-shareable (the common D3D11VA
//         case: decoder-private bind flags, KMT-only sharing, or no sharing
//         at all). A persistent pool of N shared D3D11 textures (each paired
//         with its opened D3D12 resource + keyed mutex) is kept at the
//         current resolution; the frame is CopyResource'd into the next
//         round-robin slot (one cheap GPU-GPU copy, zero CPU traffic) and the
//         slot's D3D12 resource is handed out. The pool is rebuilt only when
//         the resolution/format changes — never per frame.
//   * interop_share_d3d12_to_d3d11 goes the other way (D3D12 resource created
//     shareable -> NT handle -> ID3D11Device1::OpenSharedResource1).
//
// KEYED-MUTEX PROTOCOL (ping-pong, keys alternate 0/1 per frame — spec ST-15)
//   Producer (D3D11 writer, done internally by the pooled-copy path):
//       AcquireSync(k, 5000) -> CopyResource -> ReleaseSync(k); k ^= 1
//   Consumer (D3D12 reader, e.g. the FSR dispatch):
//       interop_acquire(mtx, k, 5000) -> use -> interop_release(mtx, k); k ^= 1
//   Each AcquireSync(k) blocks until the OTHER party's last ReleaseSync(k)
//   completes on the GPU timeline, which both synchronizes the cross-queue
//   hand-off and provides backpressure (the producer cannot overwrite a slot
//   the consumer has not finished with). Only one key is held at a time per
//   party, so the ping-pong is deadlock-free. The consumer obtains the mutex
//   by QI'ing IDXGIKeyedMutex from the returned resource (shared resources
//   expose it on both API sides).
//
// EXCEPTION SAFETY: these are internal C++ helpers, not a C ABI boundary;
// they do not throw by design (all state is RAII — ComPtr / handle wrappers),
// and the extern "C" entry points that call them are wrapped in GuardCabi /
// GuardCabiVoid in catra_gpu.cpp, so a stray std::bad_alloc still surfaces as
// CATRA_ERR_UNKNOWN instead of unwinding into P/Invoke.

#ifndef CATRA_D3D_INTEROP_H
#define CATRA_D3D_INTEROP_H

#include "catra_gpu.h"

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <cstdint>

namespace catra {

using Microsoft::WRL::ComPtr;

// ===========================================================================
// ST-13/14 compatibility surface (signatures frozen — upscale_fsr1.cpp and
// upscale_fsr4.cpp call these). Both now delegate to the interop core below:
// CreateD3D12Device returns the interop-owned D3D12 device when interop_init
// has run (AddRef'd), else creates a standalone device on the same adapter;
// ShareTexture routes through the pooled interop_share_d3d11_to_d3d12 when
// the caller's D3D12 device is the interop device, else performs a direct
// (unpooled) share on the caller-provided devices.
// ===========================================================================

// Creates a DX12 device on the same adapter as `d3d11Device` and returns it.
HRESULT CreateD3D12Device(ID3D11Device* d3d11Device,
                          ID3D12Device** out);

// Shares a D3D11 texture into a DX12 resource (NT handle + keyed mutex).
HRESULT ShareTexture(ID3D11Device* d3d11Device,
                     ID3D12Device* d3d12Device,
                     ID3D11Texture2D* source,
                     ID3D12Resource** out);

// ===========================================================================
// ST-15 internal interop API (CATRA_* return codes, not HRESULT)
// ===========================================================================

// Creates the D3D12 device + DIRECT command queue (+ command allocator,
// graphics command list and fence) on the adapter of `d3d11_device` and
// remembers the D3D11 device + its immediate context for the share/copy
// paths. Idempotent: a second call hands out the existing device/queue.
// Either out pointer may be null when the caller does not need it.
// Returns CATRA_OK / CATRA_ERR_INVALID_ARG / CATRA_ERR_DEVICE.
int interop_init(ID3D11Device* d3d11_device,
                 ID3D12Device** out_d3d12_device,
                 ID3D12CommandQueue** out_cmd_queue);

// Shares `src` into a D3D12 resource on the interop device (requires a prior
// interop_init). Zero-copy when `src` is NT-shareable; otherwise a pooled
// GPU-GPU copy (see header banner). On success writes the D3D12 resource to
// *out_d3d12_tex (caller owns the reference; for pooled slots the pool keeps
// its own) and, when out_shared_handle is non-null, a FRESH NT shared handle
// the caller must CloseHandle. *out / *out_handle stay null on failure.
int interop_share_d3d11_to_d3d12(ID3D11Texture2D* src,
                                 ID3D12Resource** out_d3d12_tex,
                                 HANDLE* out_shared_handle);

// Shares a shareable D3D12 resource back to D3D11: CreateSharedHandle (NT) on
// the resource's device, then ID3D11Device1::OpenSharedResource1 on
// `d3d11_device`. The returned texture carries the keyed mutex (QI
// IDXGIKeyedMutex). `src` must have been created with a shared heap flag,
// otherwise CreateSharedHandle fails -> CATRA_ERR_DEVICE. Caller owns *out.
int interop_share_d3d12_to_d3d11(ID3D12Resource* src,
                                 ID3D11Device* d3d11_device,
                                 ID3D11Texture2D** out_d3d11_tex);

// IDXGIKeyedMutex::AcquireSync wrapper. Returns CATRA_OK, or
// CATRA_ERR_DEVICE on timeout (spec default 5000 ms) / failure.
int interop_acquire(IDXGIKeyedMutex* mutex, uint64_t key, uint32_t timeout_ms);

// IDXGIKeyedMutex::ReleaseSync wrapper. Returns CATRA_OK / CATRA_ERR_*.
int interop_release(IDXGIKeyedMutex* mutex, uint64_t key);

// GPU-GPU CopyResource with a geometry guard (CopyResource requires matching
// dimensions/format/array/mips/sample — a mismatch is CATRA_ERR_INVALID_ARG,
// never a device removal). The copy is issued on `ctx` and completes on the
// D3D11 timeline; cross-queue consumers synchronize via the keyed mutex.
int interop_copy_d3d11(ID3D11DeviceContext* ctx,
                       ID3D11Texture2D* src,
                       ID3D11Texture2D* dst_shared);

// Tears down the pool (closing every NT handle), the command plumbing and the
// D3D12 device/queue, and releases the retained D3D11 device/context.
// Idempotent. Call BEFORE releasing the caller's D3D11 device.
void interop_shutdown();

} // namespace catra

#endif // CATRA_D3D_INTEROP_H

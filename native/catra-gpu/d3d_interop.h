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
//       AcquireSync(k, 5000) -> CopyResource -> Flush -> ReleaseSync(k); k ^= 1
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
//   SUBMISSION GUARANTEE (root cause of the 0x887A0001 device hang): the
//   producer MUST Flush() the D3D11 immediate context between CopyResource
//   and ReleaseSync(k). CopyResource only records the copy; the keyed-mutex
//   ownership transfer waits exclusively for work already SUBMITTED to the
//   GPU, so an unflushed copy leaves the D3D12 consumer free to read the
//   shared allocation mid-copy (cross-API race -> GPU hang -> device
//   removal). Flush submits without blocking (no CPU stall, no TDR impact).
//
//   POOL-REBUILD RESYNC: a resolution/format change rebuilds the pool with
//   fresh keyed mutexes and resets the producer key to 0. Consumers track the
//   pool generation (interop_pool_generation) and MUST reset their own key to
//   0 when it changes, otherwise their next AcquireSync(k) waits for a
//   Release(k) the rebuilt producer never issues (5 s timeout -> spurious
//   CATRA_ERR_DEVICE). This matters for the interpolation pipeline, where the
//   encode input alternates NV12 (decoded source) / BGRA (RIFE intermediate)
//   and rebuilds the pool every frame.
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
#include <d3d11_1.h>
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

// AMF workaround (docs/FSR_AMF_ISSUE_CONTEXT.md): qualquer resource D3D12
// criado por usuário (output de upscale FSR 1/FSR 4) é rejeitado por
// AMFContext2::CreateSurfaceFromDX12Native, mas os resources D3D12 dos slots
// do pool de interop (abertos de texturas D3D11 shared via NT handle) são
// aceitos. Este helper copia o conteúdo de `src` para o caminho pooled D3D11
// e entrega o resource D3D12 do slot — contrato de saída IDÊNTICO ao de
// interop_share_d3d11_to_d3d12 (resource AddRef'd; *out_shared_handle fica
// null — o pool mantém os handles; caller fecha/solta via RAII existente).
//
// Path: `src` shareable -> aberto direto no D3D11; `src` não-shareable
// (caso genérico, ex.: output FSR 4 em heap NONE) -> cópia via staging D3D12
// cacheada (fence wait com timeout) e o staging é aberto no D3D11. A view
// D3D11 segue para a cópia pooled GPU-GPU; se ela falhar (ex.: keyed-mutex
// ReleaseSync com DEVICE_REMOVED no driver RDNA 4 atual) o helper cai para o
// MESMO CPU round-trip do pool12 que interop_share_d3d11_to_d3d12 usa — o
// recurso entregue então não carrega keyed mutex (o consumer do encoder pula
// o ping-pong), exatamente como o caminho D3D11 atual já entrega.
//
// CONTRATO DE ENTRADA: `src` deve chegar GPU-complete (produtor fez fence
// wait), em D3D12_RESOURCE_STATE_COMMON e no device do interop (o mesmo que
// CreateD3D12Device entrega). Requer interop_init.
int interop_copy_d3d12_for_encode(ID3D12Resource* src,
                                  ID3D12Resource** out_pool_tex,
                                  HANDLE* out_shared_handle);

// IDXGIKeyedMutex::AcquireSync wrapper. Returns CATRA_OK, or
// CATRA_ERR_DEVICE on timeout (spec default 5000 ms) / failure.
int interop_acquire(IDXGIKeyedMutex* mutex, uint64_t key, uint32_t timeout_ms);

// Pool rebuild counter: incremented exactly once whenever the pooled-copy
// pool is rebuilt (resolution/format change), read under the interop lock.
// Consumers of pooled slots keep the last generation they saw and reset
// their keyed-mutex ping-pong key to 0 when it changes — a rebuilt pool has
// fresh mutexes and a producer key reset to 0, so an unsynchronized consumer
// key deadlocks against AcquireSync's 5 s timeout (see header banner,
// POOL-REBUILD RESYNC).
uint64_t interop_pool_generation();

// IDXGIKeyedMutex::ReleaseSync wrapper. Returns CATRA_OK / CATRA_ERR_*.
int interop_release(IDXGIKeyedMutex* mutex, uint64_t key);

// GPU-GPU CopyResource with a geometry guard (CopyResource requires matching
// dimensions/format/array/mips/sample — a mismatch is CATRA_ERR_INVALID_ARG,
// never a device removal). The copy is issued on `ctx` and completes on the
// D3D11 timeline. IMPORTANT: the copy is only RECORDED here; when the result
// feeds a keyed-mutex hand-off, the caller must Flush() `ctx` before
// ReleaseSync so the copy is SUBMITTED (the pooled-copy path in
// interop_share_d3d11_to_d3d12 does exactly that); cross-queue consumers
// then synchronize via the keyed mutex.
int interop_copy_d3d11(ID3D11DeviceContext* ctx,
                       ID3D11Texture2D* src,
                       ID3D11Texture2D* dst_shared);

// Tears down the pool (closing every NT handle), the command plumbing and the
// D3D12 device/queue, and releases the retained D3D11 device/context.
// Idempotent. Call BEFORE releasing the caller's D3D11 device.
void interop_shutdown();

// NV12 → BGRA conversion via full-texture CopyResource + two Map calls.
// AMD RDNA 4 workaround: bypasses av_hwframe_transfer_data (which produces
// zeros for D3D11VA NV12 decoder textures on this driver). Uses the bridge's
// D3D11 device + immediate context (must be initialized via interop_init).
// Returns a standalone BGRA texture (ArraySize==1, caller owns the ref)
// or nullptr on failure.
ComPtr<ID3D11Texture2D> Nv12ToBgraStaging(ID3D11Texture2D* src,
                                          const D3D11_TEXTURE2D_DESC& srcDesc,
                                          unsigned int targetSlice);

} // namespace catra

#endif // CATRA_D3D_INTEROP_H

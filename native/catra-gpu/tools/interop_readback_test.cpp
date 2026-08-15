// interop_readback_test.cpp — diagnostic tool (not shipped): verifies that the
// D3D11 -> D3D12 pooled share in d3d_interop.cpp actually transfers pixel data
// on the current driver. Fills a D3D11 texture with a known pattern, shares it
// via interop_share_d3d11_to_d3d12 (the exact path the pipeline uses), then
// reads the returned D3D12 resource back to CPU and prints samples.
//
// Build: added as target catra-interop-test in CMakeLists.txt.
// Usage: catra-interop-test.exe               (prints PASS/FAIL per format)
//        catra-interop-test.exe --fsr4-smoke  (SPRINT_04 subtask 02: FSR 4/3.1
//                                              zero-MV backend smoke; exits 0
//                                              with a SKIP message when the FFX
//                                              runtime is unavailable)
//        catra-interop-test.exe --encode-d3d12-workaround
//                                              (SPRINT_04 subtask 03: AMF encode
//                                              workaround smoke — user-created
//                                              D3D12 BGRA textures (shareable AND
//                                              non-shareable heap) through
//                                              interop_copy_d3d12_for_encode into
//                                              the AmfEncoder; SKIP (exit 0) when
//                                              built without CATRA_HAS_AMF)

#include "../d3d_interop.h"
#include "../encode_amf.h"
#include "../interp_rife.h"
#include "../upscale_fsr1.h"
#include "../upscale_fsr4.h"

#include <d3d11.h>
#include <d3d12.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <vector>
using Microsoft::WRL::ComPtr;

// d3d_interop.cpp links against this log shim (normally provided by
// catra_gpu.cpp); provide a stderr sink for the standalone tool.
namespace catra {
void BackendLog(int level, const char* fmt, ...)
{
    (void)level;
    va_list args;
    va_start(args, fmt);
    vfprintf(stderr, fmt, args);
    va_end(args);
    fprintf(stderr, "\n");
}
} // namespace catra

namespace {

void Fail(const char* what, unsigned long hr)
{
    fprintf(stderr, "FAIL %s hr=0x%08lX\n", what, hr);
    exit(1);
}

ComPtr<ID3D11Device> g_d11;
ComPtr<ID3D11DeviceContext> g_ctx11;
ComPtr<ID3D12Device> g_d12;
ComPtr<ID3D12CommandQueue> g_q12;
ComPtr<ID3D12CommandAllocator> g_alloc;
ComPtr<ID3D12GraphicsCommandList> g_cl;
ComPtr<ID3D12Fence> g_fence;
HANDLE g_fenceEvent = nullptr;

void InitDevices()
{
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   0, nullptr, 0, D3D11_SDK_VERSION,
                                   g_d11.GetAddressOf(), &fl, g_ctx11.GetAddressOf());
    if (FAILED(hr)) Fail("D3D11CreateDevice", hr);

    int rc = catra::interop_init(g_d11.Get(), g_d12.GetAddressOf(), g_q12.GetAddressOf());
    if (rc != 0) { fprintf(stderr, "FAIL interop_init rc=%d\n", rc); exit(1); }

    hr = g_d12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                                       IID_PPV_ARGS(g_alloc.GetAddressOf()));
    if (FAILED(hr)) Fail("CreateCommandAllocator", hr);
    hr = g_d12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_alloc.Get(),
                                  nullptr, IID_PPV_ARGS(g_cl.GetAddressOf()));
    if (FAILED(hr)) Fail("CreateCommandList", hr);
    hr = g_d12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(g_fence.GetAddressOf()));
    if (FAILED(hr)) Fail("CreateFence", hr);
    g_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
}

void GpuWait()
{
    const UINT64 v = 1;
    g_cl->Close();
    ID3D12CommandList* lists[] = { g_cl.Get() };
    g_q12->ExecuteCommandLists(1, lists);
    g_q12->Signal(g_fence.Get(), v);
    if (g_fence->GetCompletedValue() < v)
    {
        g_fence->SetEventOnCompletion(v, g_fenceEvent);
        WaitForSingleObject(g_fenceEvent, 5000);
    }
    g_alloc->Reset();
    g_cl->Reset(g_alloc.Get(), nullptr);
}

// Reads subresource 0 (and 1 for NV12) of a D3D12 resource back to CPU.
std::vector<uint8_t> ReadBack12(ID3D12Resource* res, UINT subresources,
                                std::vector<UINT64>& rowPitches)
{
    D3D12_RESOURCE_DESC desc = res->GetDesc();
    UINT nPlanes = subresources;
    std::vector<D3D12_PLACED_SUBRESOURCE_FOOTPRINT> fp(nPlanes);
    std::vector<UINT> rows(nPlanes);
    std::vector<UINT64> rowSize(nPlanes);
    UINT64 total = 0;
    g_d12->GetCopyableFootprints(&desc, 0, nPlanes, 0, fp.data(), rows.data(),
                                 rowSize.data(), &total);
    rowPitches.assign(nPlanes, 0);
    for (UINT i = 0; i < nPlanes; ++i) rowPitches[i] = fp[i].Footprint.RowPitch;

    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width = total;
    bd.Height = 1;
    bd.DepthOrArraySize = 1;
    bd.MipLevels = 1;
    bd.Format = DXGI_FORMAT_UNKNOWN;
    bd.SampleDesc.Count = 1;
    bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> buf;
    HRESULT hr = g_d12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd,
                                                D3D12_RESOURCE_STATE_COPY_DEST,
                                                nullptr, IID_PPV_ARGS(buf.GetAddressOf()));
    if (FAILED(hr)) Fail("readback buffer", hr);

    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = res;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    g_cl->ResourceBarrier(1, &b);
    for (UINT i = 0; i < nPlanes; ++i)
    {
        D3D12_TEXTURE_COPY_LOCATION dst = {};
        dst.pResource = buf.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.PlacedFootprint = fp[i];
        D3D12_TEXTURE_COPY_LOCATION src = {};
        src.pResource = res;
        // 0 = subresource in both the Windows SDK (…_SUBRESOURCE) and the
        // vcpkg directx-headers (…_SUBRESOURCE_INDEX) spellings.
        src.Type = static_cast<D3D12_TEXTURE_COPY_TYPE>(0);
        src.SubresourceIndex = i;
        g_cl->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
    }
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    g_cl->ResourceBarrier(1, &b);
    GpuWait();

    std::vector<uint8_t> out(static_cast<size_t>(total));
    uint8_t* p = nullptr;
    hr = buf->Map(0, nullptr, reinterpret_cast<void**>(&p));
    if (FAILED(hr)) Fail("readback map", hr);
    memcpy(out.data(), p, static_cast<size_t>(total));
    buf->Unmap(0, nullptr);
    return out;
}

// Fills a D3D11 texture (NV12 or BGRA) with a non-trivial pattern via staging.
ComPtr<ID3D11Texture2D> MakePattern11(DXGI_FORMAT fmt, int w, int h)
{
    D3D11_TEXTURE2D_DESC sd = {};
    sd.Width = w; sd.Height = h; sd.MipLevels = 1; sd.ArraySize = 1;
    sd.Format = fmt; sd.SampleDesc.Count = 1;
    sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = g_d11->CreateTexture2D(&sd, nullptr, staging.GetAddressOf());
    if (FAILED(hr)) Fail("staging create", hr);

    D3D11_MAPPED_SUBRESOURCE m;
    hr = g_ctx11->Map(staging.Get(), 0, D3D11_MAP_WRITE, 0, &m);
    if (FAILED(hr)) Fail("staging map", hr);

    if (fmt == DXGI_FORMAT_B8G8R8A8_UNORM)
    {
        for (int y = 0; y < h; ++y)
        {
            uint8_t* row = static_cast<uint8_t*>(m.pData) + y * m.RowPitch;
            for (int x = 0; x < w; ++x)
            {
                row[x * 4 + 0] = static_cast<uint8_t>(x & 0xFF);        // B
                row[x * 4 + 1] = static_cast<uint8_t>(y & 0xFF);        // G
                row[x * 4 + 2] = 200;                                   // R
                row[x * 4 + 3] = 255;
            }
        }
    }
    else // NV12
    {
        // Y plane: gradient
        for (int y = 0; y < h; ++y)
        {
            uint8_t* row = static_cast<uint8_t*>(m.pData) + y * m.RowPitch;
            for (int x = 0; x < w; ++x) row[x] = static_cast<uint8_t>((x + y) & 0xFF);
        }
        // UV plane at h*RowPitch: fixed chroma
        uint8_t* uv = static_cast<uint8_t*>(m.pData) + static_cast<size_t>(h) * m.RowPitch;
        for (int y = 0; y < h / 2; ++y)
        {
            uint8_t* row = uv + y * m.RowPitch;
            for (int x = 0; x < w; x += 2) { row[x] = 90; row[x + 1] = 240; }
        }
    }
    g_ctx11->Unmap(staging.Get(), 0);

    D3D11_TEXTURE2D_DESC gd = sd;
    gd.Usage = D3D11_USAGE_DEFAULT;
    gd.CPUAccessFlags = 0;
    gd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> gpu;
    hr = g_d11->CreateTexture2D(&gd, nullptr, gpu.GetAddressOf());
    if (FAILED(hr)) Fail("gpu texture create", hr);
    g_ctx11->CopyResource(gpu.Get(), staging.Get());
    g_ctx11->Flush();
    return gpu;
}

// Reads a D3D11 texture back with PROPER per-subresource Maps and prints
// Y/UV samples at several rows — isolates whether MakePattern11 / the NV12
// staging write itself is broken (independent of the D3D12 share path).
void Probe11(ID3D11Texture2D* tex, const char* name, bool nv12)
{
    D3D11_TEXTURE2D_DESC desc;
    tex->GetDesc(&desc);
    D3D11_TEXTURE2D_DESC sd = desc;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> st;
    HRESULT hr = g_d11->CreateTexture2D(&sd, nullptr, st.GetAddressOf());
    if (FAILED(hr)) { fprintf(stderr, "[%s] probe create hr=0x%08lX\n", name, hr); return; }
    g_ctx11->CopyResource(st.Get(), tex);
    // Flush + wait via Map (Map blocks until GPU copy done).
    D3D11_MAPPED_SUBRESOURCE m0;
    hr = g_ctx11->Map(st.Get(), 0, D3D11_MAP_READ, 0, &m0);
    if (FAILED(hr)) { fprintf(stderr, "[%s] map0 hr=0x%08lX\n", name, hr); return; }
    const uint8_t* y = static_cast<const uint8_t*>(m0.pData);
    fprintf(stderr, "[%s] RowPitch0=%u (w=%u h=%u)\n", name, m0.RowPitch, desc.Width, desc.Height);
    for (int row : {0, 50, 60, 100, 200, 500, 1000})
    {
        if (row < (int)desc.Height)
            fprintf(stderr, "[%s] Y(row %d, x10)=%u\n", name, row, y[row * m0.RowPitch + 10]);
    }
    g_ctx11->Unmap(st.Get(), 0);
    if (nv12)
    {
        D3D11_MAPPED_SUBRESOURCE m1;
        hr = g_ctx11->Map(st.Get(), 1, D3D11_MAP_READ, 0, &m1);
        if (SUCCEEDED(hr))
        {
            const uint8_t* uv = static_cast<const uint8_t*>(m1.pData);
            fprintf(stderr, "[%s] RowPitch1=%u U=%u V=%u (row 0)\n", name, m1.RowPitch, uv[0], uv[1]);
            fprintf(stderr, "[%s] U=%u V=%u (row 100)\n", name, uv[100 * m1.RowPitch], uv[100 * m1.RowPitch + 1]);
            g_ctx11->Unmap(st.Get(), 1);
        }
        else fprintf(stderr, "[%s] map1 hr=0x%08lX\n", name, hr);
    }
}

bool TestFormat(DXGI_FORMAT fmt, const char* name, UINT planes)
{
    const int w = 256, h = 256;
    ComPtr<ID3D11Texture2D> src = MakePattern11(fmt, w, h);
    Probe11(src.Get(), name, fmt == DXGI_FORMAT_NV12);

    ID3D12Resource* res12 = nullptr;
    HANDLE handle = nullptr;
    int rc = catra::interop_share_d3d11_to_d3d12(src.Get(), &res12, &handle);
    if (rc != 0 || !res12)
    {
        fprintf(stderr, "[%s] FAIL interop_share rc=%d\n", name, rc);
        return false;
    }

    std::vector<UINT64> pitches;
    std::vector<uint8_t> data = ReadBack12(res12, planes, pitches);

    bool ok = false;
    if (fmt == DXGI_FORMAT_B8G8R8A8_UNORM)
    {
        // sample pixel (10, 20): B=10, G=20, R=200
        const uint8_t* px = data.data() + 20 * pitches[0] + 10 * 4;
        ok = px[0] == 10 && px[1] == 20 && px[2] == 200 && px[3] == 255;
        fprintf(stderr, "[%s] pixel(10,20) = %u,%u,%u,%u (expect 10,20,200,255)\n",
                name, px[0], px[1], px[2], px[3]);
    }
    else
    {
        // Y at (10,20) = (10+20)&0xFF = 30; UV at (4,10) = 90,240
        const uint8_t y = data[20 * pitches[0] + 10];
        const uint8_t* uvBase = data.data() + static_cast<size_t>(h) * pitches[0];
        const uint8_t u = uvBase[10 * pitches[1] / pitches[1] * 0 + 0]; // first UV pair row0
        const uint8_t v = uvBase[1];
        ok = (y == 30) && (u == 90) && (v == 240);
        fprintf(stderr, "[%s] Y(10,20)=%u (expect 30); U=%u V=%u (expect 90,240)\n",
                name, y, u, v);
    }

    // also check whether the whole buffer is zero (the 'green' symptom)
    bool allZero = true;
    for (size_t i = 0; i < data.size() && i < 65536; ++i)
        if (data[i] != 0) { allZero = false; break; }
    fprintf(stderr, "[%s] first 64KB all zero: %s\n", name, allZero ? "YES (GREEN!)" : "no");

    if (handle) CloseHandle(handle);
    res12->Release();
    return ok;
}

} // namespace

// Replicates the pipeline's encode path: patterned NV12 D3D11 texture ->
// interop pooled share -> AMF HEVC encode; dumps the Annex-B packet so the
// caller can decode it to PNG and inspect the pixels the encoder saw.
bool TestAmfEncode()
{
    const int w = 1920, h = 1080;
    ComPtr<ID3D11Texture2D> src = MakePattern11(DXGI_FORMAT_NV12, w, h);

    std::unique_ptr<catra::AmfEncoder> enc;
    int rc = catra::AmfEncoder::Create(g_d12.Get(), w, h, 20000, 135.0, enc);
    if (rc != 0 || !enc)
    {
        fprintf(stderr, "[AMF] FAIL create rc=%d\n", rc);
        return false;
    }

    FILE* out = fopen("amf_test_dump.h265", "wb");
    if (!out) { fprintf(stderr, "[AMF] FAIL open dump\n"); return false; }

    // Feed the same frame several times (encoder needs a few frames to emit).
    bool anyData = false;
    for (int i = 0; i < 8; ++i)
    {
        ID3D12Resource* res12 = nullptr;
        HANDLE handle = nullptr;
        rc = catra::interop_share_d3d11_to_d3d12(src.Get(), &res12, &handle);
        if (rc != 0 || !res12) { fprintf(stderr, "[AMF] share rc=%d\n", rc); fclose(out); return false; }

        uint8_t* buf = nullptr;
        int size = 0;
        rc = enc->Encode(res12, &buf, &size);
        fprintf(stderr, "[AMF] frame %d: Encode rc=%d size=%d\n", i, rc, size);
        if (rc == 0 && size > 0) { fwrite(buf, 1, static_cast<size_t>(size), out); anyData = true; }
        if (handle) CloseHandle(handle);
        res12->Release();
    }
    uint8_t* buf = nullptr;
    int size = 0;
    if (enc->Flush(&buf, &size) == 0 && size > 0)
    {
        fwrite(buf, 1, static_cast<size_t>(size), out);
        anyData = true;
        fprintf(stderr, "[AMF] flush size=%d\n", size);
    }
    fclose(out);
    fprintf(stderr, "[AMF] dump written (anyData=%d)\n", anyData ? 1 : 0);
    return anyData;
}

// Full pipeline replication: two NV12 "decoder" frames -> RIFE DirectML
// interpolation -> interop pooled share -> AMF encode -> dump. If THIS turns
// green while TestAmfEncode (no RIFE) stays correct, the DirectML stage is
// corrupting the subsequent D3D11/D3D12 interop on this driver.
bool TestRifeEncodePath()
{
    const int w = 1920, h = 1080;
    ComPtr<ID3D11Texture2D> a = MakePattern11(DXGI_FORMAT_NV12, w, h);
    ComPtr<ID3D11Texture2D> b = MakePattern11(DXGI_FORMAT_NV12, w, h);
    Probe11(a.Get(), "RIFE-src", true);

    int ictx = -1;
    int rc = catra::InterpRifeCreate(g_d11.Get(), g_ctx11.Get(), w, h, 24.0, 135.0,
                                     CATRA_INTERP_RIFE, &ictx);
    if (rc != 0 || ictx < 0)
    {
        fprintf(stderr, "[RIFE] FAIL create rc=%d (model missing?)\n", rc);
        return false;
    }

    void* framesArray = nullptr; // receives the native void*[N] array pointer
    int count = 0;
    rc = catra::InterpRifeProcess(ictx, a.Get(), b.Get(), &framesArray, &count);
    void** frames = static_cast<void**>(framesArray);
    fprintf(stderr, "[RIFE] process rc=%d count=%d\n", rc, count);
    if (rc != 0 || count <= 0 || !frames) return false;

    std::unique_ptr<catra::AmfEncoder> enc;
    rc = catra::AmfEncoder::Create(g_d12.Get(), w, h, 20000, 135.0, enc);
    if (rc != 0) { fprintf(stderr, "[RIFE] FAIL amf create rc=%d\n", rc); return false; }

    FILE* out = fopen("rife_dump.h265", "wb");
    auto encodeTex = [&](ID3D11Texture2D* t) {
        ID3D12Resource* res = nullptr;
        HANDLE handle = nullptr;
        int r2 = catra::interop_share_d3d11_to_d3d12(t, &res, &handle);
        if (r2 != 0 || !res) { fprintf(stderr, "[RIFE] share rc=%d\n", r2); return; }
        uint8_t* buf = nullptr;
        int size = 0;
        r2 = enc->Encode(res, &buf, &size);
        if (r2 == 0 && size > 0) fwrite(buf, 1, (size_t)size, out);
        if (handle) CloseHandle(handle);
        res->Release();
    };

    encodeTex(a.Get());
    for (int i = 0; i < count; ++i)
        encodeTex(static_cast<ID3D11Texture2D*>(frames[i]));
    uint8_t* buf = nullptr;
    int size = 0;
    if (enc->Flush(&buf, &size) == 0 && size > 0) fwrite(buf, 1, (size_t)size, out);
    fclose(out);

    for (int i = 0; i < count; ++i)
        static_cast<ID3D11Texture2D*>(frames[i])->Release();
    delete[] frames; // same CRT heap: interp_rife.cpp allocates with new void*[N]
    (void)b;
    catra::InterpRifeDestroy(ictx);
    fprintf(stderr, "[RIFE] dump written\n");
    return true;
}

// SPRINT_04 subtask 02 — FSR 4/3.1 zero-MV backend smoke: 320x180 -> 640x360
// (the C ABI method=2 path, driven through the same C++ classes), gradient
// input, N dispatches including a scene-cut reset, then readback + contract
// checks: dimensions, BGRA output format (PO decision D-PO-4) and
// non-corrupted pixels. GRACEFUL SKIP (exit 0 + message) when the FFX
// runtime is unavailable (no loader/DLLs/adapter) — the skip keeps CI green.
bool TestFsr4Smoke(bool& skipped)
{
    skipped = false;
    if (!catra::Fsr4IsAvailable(g_d11.Get()))
    {
        fprintf(stderr, "[FSR4] SKIP: FFX upscale runtime unavailable on this machine\n");
        skipped = true;
        return true;
    }

    const int sw = 320, sh = 180, dw = 640, dh = 360;
    ComPtr<ID3D11Texture2D> src = MakePattern11(DXGI_FORMAT_B8G8R8A8_UNORM, sw, sh);

    std::unique_ptr<catra::Fsr4Upscaler> up;
    int rc = catra::Fsr4Upscaler::Create(g_d11.Get(), sw, sh, dw, dh,
                                         catra::UpscaleQualityMode::Quality, up);
    if (rc != 0 || !up)
    {
        fprintf(stderr, "[FSR4] FAIL create rc=%d\n", rc);
        return false;
    }

    // Three temporal frames; the third follows a RequestSceneCut (reset=true).
    for (int i = 0; i < 3; ++i)
    {
        if (i == 2)
        {
            rc = up->RequestSceneCut();
            if (rc != 0)
            {
                fprintf(stderr, "[FSR4] FAIL scene-cut reset rc=%d\n", rc);
                return false;
            }
        }
        ID3D12Resource* frameOut = nullptr;
        rc = up->Process(src.Get(), &frameOut);
        if (rc != 0 || !frameOut)
        {
            fprintf(stderr, "[FSR4] FAIL process frame %d rc=%d\n", i, rc);
            return false;
        }
        frameOut->Release();
    }

    // Final frame: the one we read back and validate.
    ID3D12Resource* out = nullptr;
    rc = up->Process(src.Get(), &out);
    if (rc != 0 || !out)
    {
        fprintf(stderr, "[FSR4] FAIL final process rc=%d\n", rc);
        return false;
    }

    bool ok = true;
    D3D12_RESOURCE_DESC od = out->GetDesc();
    if (od.Width != static_cast<UINT>(dw) || od.Height != static_cast<UINT>(dh))
    {
        fprintf(stderr, "[FSR4] FAIL output dims %llux%u (expect %dx%d)\n",
                static_cast<unsigned long long>(od.Width), od.Height, dw, dh);
        ok = false;
    }
    if (od.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
    {
        fprintf(stderr, "[FSR4] FAIL output format %d (expect B8G8R8A8_UNORM — D-PO-4)\n",
                static_cast<int>(od.Format));
        ok = false;
    }
    if (ok)
    {
        fprintf(stderr, "[FSR4] output %dx%d format=BGRA (D-PO-4) OK\n", dw, dh);
    }

    if (ok)
    {
        std::vector<UINT64> pitches;
        auto data = ReadBack12(out, 1, pitches);
        const UINT rp = static_cast<UINT>(pitches[0]);
        long nonblack = 0, total = 0;
        for (int y = 0; y < dh; y += 4)
        {
            for (int x = 0; x < dw; x += 4)
            {
                const uint8_t* px = data.data() + static_cast<size_t>(y) * rp +
                                    static_cast<size_t>(x) * 4;
                ++total;
                if (px[0] > 8 || px[1] > 8 || px[2] > 8) ++nonblack;
            }
        }
        const uint8_t* c = data.data() + static_cast<size_t>(dh / 2) * rp +
                           static_cast<size_t>(dw / 2) * 4;
        fprintf(stderr, "[FSR4] centre=%u,%u,%u,%u nonblack=%ld/%ld\n",
                c[0], c[1], c[2], c[3], nonblack, total);
        ok = nonblack > total / 2;
        fprintf(stderr, "[FSR4] %s\n", ok ? "PASS" : "FAIL-CORRUPT");
    }
    out->Release();
    return ok;
}

// SPRINT_04 subtask 03 — AMF encode workaround smoke. Mirrors the pipeline's
// encode boundary for D3D12 inputs (FSR 1/FSR 4 upscale outputs): a
// user-created BGRA D3D12 texture goes through
// interop_copy_d3d12_for_encode (the generic D3D12 -> D3D11 pool copy) and
// the returned pool-slot resource is fed to AmfEncoder::Encode.
//
// Two variants cover both helper routes:
//   (a) D3D12_HEAP_FLAG_SHARED  -> CreateSharedHandle probe succeeds, the
//       texture is opened directly on the D3D11 side;
//   (b) D3D12_HEAP_FLAG_NONE    -> probe fails, the cached shared staging
//       copy path runs (FSR 4 output case).
// Pass criterion per variant: every Encode returns CATRA_OK and the total
// encoded byte count is > 0 (the dump is discarded — pixel checks live in the
// other modes).
ComPtr<ID3D12Resource> MakePattern12(int w, int h, D3D12_HEAP_FLAGS heapFlags)
{
    D3D12_RESOURCE_DESC td = {};
    td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    td.Width = static_cast<UINT>(w);
    td.Height = static_cast<UINT>(h);
    td.DepthOrArraySize = 1;
    td.MipLevels = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    D3D12_HEAP_PROPERTIES hp = {};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    ComPtr<ID3D12Resource> tex;
    HRESULT hr = g_d12->CreateCommittedResource(&hp, heapFlags, &td,
                                                D3D12_RESOURCE_STATE_COMMON, nullptr,
                                                IID_PPV_ARGS(tex.GetAddressOf()));
    if (FAILED(hr)) Fail("workaround src create", hr);

    // Same gradient as MakePattern11(BGRA), staged through an upload buffer.
    UINT rows = 0;
    UINT64 rowSize = 0, total = 0;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
    g_d12->GetCopyableFootprints(&td, 0, 1, 0, &fp, &rows, &rowSize, &total);
    D3D12_HEAP_PROPERTIES uhp = {};
    uhp.Type = D3D12_HEAP_TYPE_UPLOAD;
    D3D12_RESOURCE_DESC bd = {};
    bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width = total;
    bd.Height = 1;
    bd.DepthOrArraySize = 1;
    bd.MipLevels = 1;
    bd.SampleDesc.Count = 1;
    bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> up;
    hr = g_d12->CreateCommittedResource(&uhp, D3D12_HEAP_FLAG_NONE, &bd,
                                        D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                        IID_PPV_ARGS(&up));
    if (FAILED(hr)) Fail("workaround upload create", hr);
    uint8_t* mp = nullptr;
    up->Map(0, nullptr, reinterpret_cast<void**>(&mp));
    for (int y = 0; y < h; ++y)
    {
        for (int x = 0; x < w; ++x)
        {
            uint8_t* p = mp + fp.Offset + static_cast<size_t>(y) * fp.Footprint.RowPitch + x * 4;
            p[0] = static_cast<uint8_t>(x & 0xFF); // B
            p[1] = static_cast<uint8_t>(y & 0xFF); // G
            p[2] = 200;                            // R
            p[3] = 255;
        }
    }
    up->Unmap(0, nullptr);

    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = tex.Get();
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    g_cl->ResourceBarrier(1, &b);
    D3D12_TEXTURE_COPY_LOCATION dstL = {};
    dstL.pResource = tex.Get();
    dstL.Type = static_cast<D3D12_TEXTURE_COPY_TYPE>(0);
    dstL.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION srcL = {};
    srcL.pResource = up.Get();
    srcL.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    srcL.PlacedFootprint = fp;
    g_cl->CopyTextureRegion(&dstL, 0, 0, 0, &srcL, nullptr);
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    g_cl->ResourceBarrier(1, &b);
    GpuWait(); // fence-waited, left in COMMON — the workaround input contract
    return tex;
}

bool TestEncodeD3D12Workaround(bool& skipped)
{
    skipped = false;
    if (!catra::AmfIsCompiled())
    {
        fprintf(stderr, "[WA] SKIP (no AMF — built without CATRA_HAS_AMF)\n");
        skipped = true;
        return true;
    }

    const int w = 640, h = 360; // small but HEVC-legal (320x180 is rejected by AMF Init)
    struct Variant { const char* name; D3D12_HEAP_FLAGS flags; };
    const Variant variants[2] = {
        { "shared", D3D12_HEAP_FLAG_SHARED },
        { "none->staging", D3D12_HEAP_FLAG_NONE },
    };

    for (const Variant& v : variants)
    {
        ComPtr<ID3D12Resource> src = MakePattern12(w, h, v.flags);

        std::unique_ptr<catra::AmfEncoder> enc;
        int rc = catra::AmfEncoder::Create(g_d12.Get(), w, h, 5000, 25.0, enc);
        if (rc != 0 || !enc)
        {
            fprintf(stderr, "[WA] FAIL encoder create (%s) rc=%d\n", v.name, rc);
            return false;
        }

        long long totalBytes = 0;
        bool frameOk = true;
        for (int i = 0; i < 8; ++i)
        {
            ID3D12Resource* pooled = nullptr;
            HANDLE handle = nullptr;
            rc = catra::interop_copy_d3d12_for_encode(src.Get(), &pooled, &handle);
            if (rc != 0 || pooled == nullptr)
            {
                fprintf(stderr, "[WA] FAIL workaround copy (%s) frame %d rc=%d\n", v.name, i, rc);
                frameOk = false;
                break;
            }

            uint8_t* buf = nullptr;
            int size = 0;
            const int erc = enc->Encode(pooled, &buf, &size);
            fprintf(stderr, "[WA] %s frame %d: Encode rc=%d size=%d\n", v.name, i, erc, size);
            if (erc != 0)
            {
                fprintf(stderr, "[WA] FAIL Encode (%s) frame %d rc=%d\n", v.name, i, erc);
                frameOk = false;
            }
            else if (size > 0)
            {
                totalBytes += size;
            }
            if (handle != nullptr) CloseHandle(handle);
            pooled->Release();
            if (!frameOk) break;
        }
        if (!frameOk) return false;

        uint8_t* buf = nullptr;
        int size = 0;
        rc = enc->Flush(&buf, &size);
        if (rc == 0 && size > 0)
        {
            totalBytes += size;
        }
        fprintf(stderr, "[WA] %s flush rc=%d size=%d totalBytes=%lld\n",
                v.name, rc, size, totalBytes);
        if (totalBytes <= 0)
        {
            fprintf(stderr, "[WA] FAIL no encoded bytes (%s)\n", v.name);
            return false;
        }
        enc.reset();
    }

    fprintf(stderr, "[WA] PASS (both variants encoded via the pool path)\n");
    return true;
}

void DumpD3D12ValidationMessages()
{
    ComPtr<ID3D12InfoQueue> iq;
    if (FAILED(g_d12->QueryInterface(IID_PPV_ARGS(&iq))) || !iq)
    {
        fprintf(stderr, "[diag] no ID3D12InfoQueue\n");
        return;
    }
    UINT64 n = iq->GetNumStoredMessages();
    fprintf(stderr, "[diag] %llu D3D12 validation messages\n",
            (unsigned long long)n);
    for (UINT64 i = 0; i < n && i < 40; ++i)
    {
        SIZE_T len = 0;
        iq->GetMessage(i, nullptr, &len);
        std::vector<char> buf(len);
        if (SUCCEEDED(iq->GetMessage(i, reinterpret_cast<D3D12_MESSAGE*>(buf.data()), &len)))
        {
            auto* msg = reinterpret_cast<D3D12_MESSAGE*>(buf.data());
            fprintf(stderr, "[diag] #%llu sev=%d id=%d: %.*s\n",
                    (unsigned long long)i, (int)msg->Severity, (int)msg->ID,
                    (int)msg->DescriptionByteLength, msg->pDescription);
        }
    }
    iq->ClearStoredMessages();
}

bool TestMinimalCompute()
{
    // Mirrors FSR1's exact resource/descriptor setup but with a trivial shader
    // that writes a constant. Isolates whether ANY D3D12 compute UAV write
    // lands on this driver.
    ComPtr<ID3D12Device> dev = g_d12;
    const char* hlsl =
        "RWTexture2D<float4> Out : register(u0);\n"
        "[numthreads(16,16,1)] void CSMain(uint3 gid : SV_DispatchThreadID)\n"
        "{ Out[int2(gid.xy)] = float4(1.0,0.0,1.0,1.0); }\n";
    ComPtr<ID3DBlob> blob, err;
    HRESULT hr = D3DCompile(hlsl, strlen(hlsl), "min", nullptr, nullptr, "CSMain",
                            "cs_5_1", 0, 0, blob.GetAddressOf(), err.GetAddressOf());
    if (FAILED(hr)) { fprintf(stderr, "[MIN] compile fail\n"); return false; }

    // Root sig: single UAV descriptor table.
    D3D12_DESCRIPTOR_RANGE range = {};
    range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    range.NumDescriptors = 1;
    range.BaseShaderRegister = 0;
    D3D12_ROOT_PARAMETER param = {};
    param.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    param.DescriptorTable.NumDescriptorRanges = 1;
    param.DescriptorTable.pDescriptorRanges = &range;
    D3D12_ROOT_SIGNATURE_DESC rsd = {};
    rsd.NumParameters = 1; rsd.pParameters = &param;
    ComPtr<ID3DBlob> rsBlob, rsErr;
    D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1,
                                rsBlob.GetAddressOf(), rsErr.GetAddressOf());
    ComPtr<ID3D12RootSignature> rs;
    dev->CreateRootSignature(0, rsBlob->GetBufferPointer(), rsBlob->GetBufferSize(),
                             IID_PPV_ARGS(&rs));
    D3D12_COMPUTE_PIPELINE_STATE_DESC psd = {};
    psd.pRootSignature = rs.Get();
    psd.CS = { blob->GetBufferPointer(), blob->GetBufferSize() };
    ComPtr<ID3D12PipelineState> pso;
    dev->CreateComputePipelineState(&psd, IID_PPV_ARGS(&pso));

    const UINT W = 256, H = 256;
    D3D12_RESOURCE_DESC od = {};
    od.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    od.Width = W; od.Height = H; od.DepthOrArraySize = 1; od.MipLevels = 1;
    od.Format = DXGI_FORMAT_B8G8R8A8_UNORM; od.SampleDesc.Count = 1;
    od.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    D3D12_HEAP_PROPERTIES dhp = {}; dhp.Type = D3D12_HEAP_TYPE_DEFAULT;
    ComPtr<ID3D12Resource> uav;
    dev->CreateCommittedResource(&dhp, D3D12_HEAP_FLAG_NONE, &od,
                                 D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
                                 IID_PPV_ARGS(&uav));
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = 1;
    hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    ComPtr<ID3D12DescriptorHeap> heap;
    dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap));
    dev->CreateUnorderedAccessView(uav.Get(), nullptr, nullptr,
                                   heap->GetCPUDescriptorHandleForHeapStart());

    ComPtr<ID3D12CommandAllocator> alloc;
    ComPtr<ID3D12GraphicsCommandList> cl;
    ComPtr<ID3D12CommandQueue> q;
    D3D12_COMMAND_QUEUE_DESC qd = {}; qd.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q));
    dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&alloc));
    dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, alloc.Get(),
                           pso.Get(), IID_PPV_ARGS(&cl));
    cl->SetComputeRootSignature(rs.Get());
    ID3D12DescriptorHeap* heaps[] = { heap.Get() };
    cl->SetDescriptorHeaps(1, heaps);
    cl->SetComputeRootDescriptorTable(0, heap->GetGPUDescriptorHandleForHeapStart());
    cl->Dispatch(W / 16, H / 16, 1);
    cl->Close();
    ID3D12CommandList* lists[] = { cl.Get() };
    q->ExecuteCommandLists(1, lists);
    ComPtr<ID3D12Fence> f; dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f));
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    q->Signal(f.Get(), 1);
    if (f->GetCompletedValue() < 1) { f->SetEventOnCompletion(1, ev); WaitForSingleObject(ev, 5000); }

    // Read back.
    std::vector<UINT64> rp; auto data = ReadBack12(uav.Get(), 1, rp);
    const uint8_t* px = data.data() + (size_t)(H / 2) * rp[0] + (size_t)(W / 2) * 4;
    fprintf(stderr, "[MIN] centre=%u,%u,%u,%u (expect 255,0,255,255)\n",
            px[0], px[1], px[2], px[3]);
    CloseHandle(ev);
    return px[0] > 200 && px[2] > 200;
}

bool TestSrvCompute()
{
    // Minimal compute + SRV read + input COMMON->NON_PIXEL barrier, mirroring
    // FSR1's additions over TestMinimalCompute. Isolates whether the SRV/barrier
    // combo is what kills the UAV write on this driver.
    ComPtr<ID3D12Device> dev = g_d12;
    const char* hlsl =
        "Texture2D<float4> In : register(t0);\n"
        "RWTexture2D<float4> Out : register(u0);\n"
        "[numthreads(16,16,1)] void CSMain(uint3 gid : SV_DispatchThreadID)\n"
        "{ Out[int2(gid.xy)] = In.Load(int3((int)gid.x,(int)gid.y,0)); }\n";
    ComPtr<ID3DBlob> blob, err;
    D3DCompile(hlsl, strlen(hlsl), "srv", nullptr, nullptr, "CSMain", "cs_5_1", 0, 0,
               blob.GetAddressOf(), err.GetAddressOf());

    D3D12_DESCRIPTOR_RANGE ranges[2] = {};
    ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; ranges[0].NumDescriptors = 1;
    ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; ranges[1].NumDescriptors = 1;
    D3D12_ROOT_PARAMETER param = {};
    param.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    param.DescriptorTable.NumDescriptorRanges = 2;
    param.DescriptorTable.pDescriptorRanges = ranges;
    D3D12_ROOT_SIGNATURE_DESC rsd = {}; rsd.NumParameters = 1; rsd.pParameters = &param;
    ComPtr<ID3DBlob> rsBlob, rsErr;
    D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, rsBlob.GetAddressOf(), rsErr.GetAddressOf());
    ComPtr<ID3D12RootSignature> rs;
    dev->CreateRootSignature(0, rsBlob->GetBufferPointer(), rsBlob->GetBufferSize(), IID_PPV_ARGS(&rs));
    D3D12_COMPUTE_PIPELINE_STATE_DESC psd = {}; psd.pRootSignature = rs.Get();
    psd.CS = { blob->GetBufferPointer(), blob->GetBufferSize() };
    ComPtr<ID3D12PipelineState> pso; dev->CreateComputePipelineState(&psd, IID_PPV_ARGS(&pso));

    const UINT W = 256, H = 256;
    // Input texture: upload a pattern via a staging->default copy on D3D12.
    D3D12_HEAP_PROPERTIES dhp = {}; dhp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC td = {};
    td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; td.Width = W; td.Height = H;
    td.DepthOrArraySize = 1; td.MipLevels = 1; td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    ComPtr<ID3D12Resource> inTex;
    dev->CreateCommittedResource(&dhp, D3D12_HEAP_FLAG_NONE, &td,
                                 D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&inTex));
    // Fill via upload buffer.
    D3D12_RESOURCE_DESC rd = inTex->GetDesc();
    UINT rows; UINT64 rowSize, total; D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp;
    dev->GetCopyableFootprints(&rd, 0, 1, 0, &fp, &rows, &rowSize, &total);
    D3D12_HEAP_PROPERTIES uhp = {}; uhp.Type = D3D12_HEAP_TYPE_UPLOAD;
    D3D12_RESOURCE_DESC bd = {}; bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bd.Width = total; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
    bd.SampleDesc.Count = 1; bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> up; dev->CreateCommittedResource(&uhp, D3D12_HEAP_FLAG_NONE, &bd,
        D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&up));
    uint8_t* mp; up->Map(0, nullptr, (void**)&mp);
    for (UINT y = 0; y < H; y++)
        for (UINT x = 0; x < W; x++) {
            uint8_t* p = mp + fp.Offset + y * fp.Footprint.RowPitch + x * 4;
            p[0] = (uint8_t)x; p[1] = (uint8_t)y; p[2] = 200; p[3] = 255;
        }
    up->Unmap(0, nullptr);

    D3D12_RESOURCE_DESC od = td; od.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    ComPtr<ID3D12Resource> uav;
    dev->CreateCommittedResource(&dhp, D3D12_HEAP_FLAG_NONE, &od,
                                 D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr, IID_PPV_ARGS(&uav));

    D3D12_DESCRIPTOR_HEAP_DESC hd = {}; hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    hd.NumDescriptors = 2; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    ComPtr<ID3D12DescriptorHeap> heap; dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap));
    UINT inc = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    D3D12_CPU_DESCRIPTOR_HANDLE h0 = heap->GetCPUDescriptorHandleForHeapStart();
    D3D12_SHADER_RESOURCE_VIEW_DESC sd = {}; sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; sd.Texture2D.MipLevels = 1;
    dev->CreateShaderResourceView(inTex.Get(), &sd, h0);
    D3D12_CPU_DESCRIPTOR_HANDLE h1 = { h0.ptr + inc };
    dev->CreateUnorderedAccessView(uav.Get(), nullptr, nullptr, h1);

    ComPtr<ID3D12CommandQueue> q; D3D12_COMMAND_QUEUE_DESC qd = {}; qd.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
    dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&q));
    ComPtr<ID3D12CommandAllocator> alloc;
    dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&alloc));
    ComPtr<ID3D12GraphicsCommandList> cl;
    dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, alloc.Get(), pso.Get(), IID_PPV_ARGS(&cl));
    cl->SetComputeRootSignature(rs.Get());
    ID3D12DescriptorHeap* heaps[] = { heap.Get() };
    cl->SetDescriptorHeaps(1, heaps);
    cl->SetComputeRootDescriptorTable(0, heap->GetGPUDescriptorHandleForHeapStart());
    // Upload the pattern into inTex first: COMMON->COPY_DEST, copy,
    // COPY_DEST->NON_PIXEL (then the dispatch reads it).
    D3D12_RESOURCE_BARRIER ub = {}; ub.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    ub.Transition.pResource = inTex.Get(); ub.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    ub.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    ub.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    cl->ResourceBarrier(1, &ub);
    D3D12_TEXTURE_COPY_LOCATION dstL = {}; dstL.pResource = inTex.Get();
    dstL.Type = static_cast<D3D12_TEXTURE_COPY_TYPE>(0); dstL.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION srcL = {}; srcL.pResource = up.Get();
    srcL.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; srcL.PlacedFootprint = fp;
    cl->CopyTextureRegion(&dstL, 0, 0, 0, &srcL, nullptr);
    D3D12_RESOURCE_BARRIER b = {}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = inTex.Get(); b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    cl->ResourceBarrier(1, &b);
    cl->Dispatch(W / 16, H / 16, 1);
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    cl->ResourceBarrier(1, &b);
    cl->Close();
    ID3D12CommandList* lists[] = { cl.Get() };
    q->ExecuteCommandLists(1, lists);
    ComPtr<ID3D12Fence> f; dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&f));
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    q->Signal(f.Get(), 1);
    if (f->GetCompletedValue() < 1) { f->SetEventOnCompletion(1, ev); WaitForSingleObject(ev, 5000); }

    std::vector<UINT64> rp; auto data = ReadBack12(uav.Get(), 1, rp);
    const uint8_t* px = data.data() + (size_t)(H / 2) * rp[0] + (size_t)(W / 2) * 4;
    fprintf(stderr, "[SRV] centre=%u,%u,%u,%u (expect ~128,128,200,255)\n", px[0], px[1], px[2], px[3]);
    CloseHandle(ev);
    return px[2] > 100;
}

bool TestFsr1Upscale()
{
    const int sw = 960, sh = 540, dw = 1280, dh = 720;
    ComPtr<ID3D11Texture2D> src = MakePattern11(DXGI_FORMAT_B8G8R8A8_UNORM, sw, sh);

    std::unique_ptr<catra::Fsr1Upscaler> up;
    int rc = catra::Fsr1Upscaler::Create(g_d11.Get(), sw, sh, dw, dh, up);
    if (rc != 0 || !up) { fprintf(stderr, "[FSR1] FAIL create rc=%d\n", rc); return false; }

    ID3D12Resource* out = nullptr;
    rc = up->Process(src.Get(), &out);
    if (rc != 0 || !out) { fprintf(stderr, "[FSR1] FAIL process rc=%d\n", rc); return false; }
    {
        HRESULT rem = g_d12->GetDeviceRemovedReason();
        fprintf(stderr, "[FSR1] post-process deviceRemoved=0x%08lX\n",
                (unsigned long)rem);
    }

    std::vector<UINT64> pitches;
    auto data = ReadBack12(out, 1, pitches);
    // Sample centre + a few points; count non-black pixels.
    const UINT rp = static_cast<UINT>(pitches[0]);
    long nonblack = 0, total = 0;
    for (int y = 0; y < dh; y += 8)
    {
        for (int x = 0; x < dw; x += 8)
        {
            const uint8_t* px = data.data() + (size_t)y * rp + (size_t)x * 4;
            ++total;
            if (px[0] > 8 || px[1] > 8 || px[2] > 8) ++nonblack;
        }
    }
    const uint8_t* c = data.data() + (size_t)(dh / 2) * rp + (size_t)(dw / 2) * 4;
    fprintf(stderr, "[FSR1] centre=%u,%u,%u,%u nonblack=%ld/%ld\n",
            c[0], c[1], c[2], c[3], nonblack, total);
    out->Release();
    bool ok = nonblack > total / 2;
    fprintf(stderr, "[FSR1] %s\n", ok ? "PASS" : "FAIL-BLACK");
    return ok;
}

int main(int argc, char** argv)
{
    const bool fsr4Smoke = (argc > 1 && strcmp(argv[1], "--fsr4-smoke") == 0);
    const bool encodeWorkaround = (argc > 1 && strcmp(argv[1], "--encode-d3d12-workaround") == 0);

    // D3D12 debug layer BEFORE any device creation: the FSR1 dispatch drops
    // UAV writes on this driver and we need the validation messages to see
    // why. Messages are pulled from ID3D12InfoQueue after Process().
    ComPtr<ID3D12Debug> dbg;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dbg))))
    {
        dbg->EnableDebugLayer();
        fprintf(stderr, "[diag] D3D12 debug layer enabled\n");
    }
    InitDevices();

    if (fsr4Smoke)
    {
        bool skipped = false;
        const bool ok = TestFsr4Smoke(skipped);
        DumpD3D12ValidationMessages();
        fprintf(stderr, "RESULT: FSR4=%s\n",
                skipped ? "SKIP" : (ok ? "PASS" : "FAIL"));
        // A graceful SKIP is exit 0: missing FFX runtime is a supported state.
        return ok ? 0 : 2;
    }

    if (encodeWorkaround)
    {
        bool skipped = false;
        const bool ok = TestEncodeD3D12Workaround(skipped);
        DumpD3D12ValidationMessages();
        if (skipped)
        {
            // Graceful SKIP (exit 0): no AMF in this build is a supported state.
            fprintf(stderr, "RESULT: ENCODE_D3D12_WORKAROUND=SKIP\n");
            return 0;
        }
        if (!ok)
        {
            fprintf(stderr, "RESULT: ENCODE_D3D12_WORKAROUND=FAIL\n");
            return 2;
        }
        fprintf(stderr, "RESULT: ENCODE_D3D12_WORKAROUND=PASS\n");
        // Fall through to the existing suite: pool/copy regression.
    }

    bool minc = TestMinimalCompute();
    fprintf(stderr, "[MIN] %s\n", minc ? "PASS" : "FAIL");
    bool fsr1 = TestFsr1Upscale();
    DumpD3D12ValidationMessages();
    bool bgra = TestFormat(DXGI_FORMAT_B8G8R8A8_UNORM, "BGRA", 1);
    bool nv12 = TestFormat(DXGI_FORMAT_NV12, "NV12", 2);
    bool amf = TestAmfEncode();
    bool rife = TestRifeEncodePath();
    fprintf(stderr, "RESULT: BGRA=%s NV12=%s AMF=%s RIFE=%s FSR1=%s\n", bgra ? "PASS" : "FAIL",
            nv12 ? "PASS" : "FAIL", amf ? "PASS" : "FAIL", rife ? "PASS" : "FAIL", fsr1 ? "PASS" : "FAIL");
    return (bgra && nv12 && amf && rife && fsr1) ? 0 : 2;
}

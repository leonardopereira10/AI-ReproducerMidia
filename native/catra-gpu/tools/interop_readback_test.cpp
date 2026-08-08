// interop_readback_test.cpp — diagnostic tool (not shipped): verifies that the
// D3D11 -> D3D12 pooled share in d3d_interop.cpp actually transfers pixel data
// on the current driver. Fills a D3D11 texture with a known pattern, shares it
// via interop_share_d3d11_to_d3d12 (the exact path the pipeline uses), then
// reads the returned D3D12 resource back to CPU and prints samples.
//
// Build: added as target catra-interop-test in CMakeLists.txt.
// Usage: catra-interop-test.exe   (prints PASS/FAIL per format)

#include "../d3d_interop.h"
#include "../encode_amf.h"
#include "../interp_rife.h"

#include <d3d11.h>
#include <d3d12.h>
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

int main()
{
    InitDevices();
    bool bgra = TestFormat(DXGI_FORMAT_B8G8R8A8_UNORM, "BGRA", 1);
    bool nv12 = TestFormat(DXGI_FORMAT_NV12, "NV12", 2);
    bool amf = TestAmfEncode();
    bool rife = TestRifeEncodePath();
    fprintf(stderr, "RESULT: BGRA=%s NV12=%s AMF=%s RIFE=%s\n", bgra ? "PASS" : "FAIL",
            nv12 ? "PASS" : "FAIL", amf ? "PASS" : "FAIL", rife ? "PASS" : "FAIL");
    return (bgra && nv12 && amf && rife) ? 0 : 2;
}

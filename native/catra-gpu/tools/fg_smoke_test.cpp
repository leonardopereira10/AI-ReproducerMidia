// fg_smoke_test.cpp — E2E native smoke for the catra_fg_* Frame Generation
// playback module (SPRINT_04 subtask 04). NOT shipped / NOT installed.
//
// Build: target catra-fg-smoke-test in CMakeLists.txt (compiles the bridge
// sources into the exe — same pattern as catra-interop-test — and POST_BUILD
// copies the 8 FFX runtime DLLs next to the exe).
//
// Usage: catra-fg-smoke-test.exe [--expect-unavailable]
//   (no args)             normal run. If the FFX runtime / a compatible GPU is
//                         absent the test SKIPS gracefully (exit 0) so CI
//                         machines without the DLLs/GPU do not break.
//   --expect-unavailable  asserts catra_is_fg_available()==0 AND that
//                         catra_fg_create returns an error code WITHOUT
//                         crashing — the acceptance criterion for missing
//                         DLLs. Run from a directory WITHOUT the 8 FFX DLLs.
//
// Flow (normal, available): create a 640x360 windowed HWND + a D3D11 device,
// catra_init, catra_fg_create on the HWND, install the present observer
// (counts rendered + generated presents), submit 90 BGRA gradient frames at
// ~30 fps, require presents >= 1.5x90 (expected ~2x on a 60 Hz display), then
// resize to 800x450 + 10 more presents, exercise the no-op resize and the
// invalid-arg present, and finally a 3x create/destroy churn with a stable
// GDI-object count. Prints PASS and returns 0 on success.

#include "../catra_gpu.h"   // flat C ABI + catra_fg.h (CATRA_ERR_*, CATRA_OK)
#include "../catra_fg.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace {

// Present counters, bumped by the FFX present callback (runtime threads).
std::atomic<uint64_t> g_presents{0};       // rendered + generated
std::atomic<uint64_t> g_generated{0};      // generated only

void PresentObserver(bool is_generated_frame, void* user)
{
    (void)user;
    g_presents.fetch_add(1, std::memory_order_relaxed);
    if (is_generated_frame)
    {
        g_generated.fetch_add(1, std::memory_order_relaxed);
    }
}

// Forward bridge diagnostics to stdout so the smoke output is self-contained.
void LogSink(const char* msg, int level)
{
    static const char* names[] = {"DBG", "INF", "WRN", "ERR"};
    const int idx = (level >= 0 && level <= 3) ? level : 3;
    printf("[bridge %s] %s\n", names[idx], msg);
    fflush(stdout);
}

void PumpMessages()
{
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    return DefWindowProcW(hwnd, msg, wp, lp);
}

HWND CreateTestWindow(int w, int h)
{
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CatraFgSmokeWindow";
    if (RegisterClassExW(&wc) == 0)
    {
        fprintf(stderr, "FAIL RegisterClassExW err=%lu\n", GetLastError());
        return nullptr;
    }
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"catra-fg-smoke",
                                WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                                w, h, nullptr, nullptr, wc.hInstance, nullptr);
    if (hwnd == nullptr)
    {
        fprintf(stderr, "FAIL CreateWindowExW err=%lu\n", GetLastError());
        return nullptr;
    }
    ShowWindow(hwnd, SW_SHOW);
    return hwnd;
}

// Creates a BGRA gradient frame the pipeline can present. `value` offsets the
// gradient so consecutive frames differ (a static frame would hide pacing bugs).
ComPtr<ID3D11Texture2D> MakeFrame(ID3D11Device* device, int w, int h, int value)
{
    std::vector<uint32_t> pixels(static_cast<size_t>(w) * static_cast<size_t>(h));
    for (int y = 0; y < h; ++y)
    {
        for (int x = 0; x < w; ++x)
        {
            const uint32_t r = static_cast<uint32_t>((x + value) & 0xFF);
            const uint32_t g = static_cast<uint32_t>((y + value) & 0xFF);
            const uint32_t b = static_cast<uint32_t>((x ^ y) & 0xFF);
            pixels[static_cast<size_t>(y) * w + x] =
                0xFF000000u | (b << 16) | (g << 8) | r; // BGRA
        }
    }

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(w);
    desc.Height = static_cast<UINT>(h);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    D3D11_SUBRESOURCE_DATA init = {};
    init.pSysMem = pixels.data();
    init.SysMemPitch = static_cast<UINT>(w) * 4;

    ComPtr<ID3D11Texture2D> tex;
    const HRESULT hr = device->CreateTexture2D(&desc, &init, tex.GetAddressOf());
    if (FAILED(hr))
    {
        fprintf(stderr, "FAIL CreateTexture2D hr=0x%08lX\n",
                static_cast<unsigned long>(hr));
        return nullptr;
    }
    return tex;
}

} // namespace

int main(int argc, char** argv)
{
    printf("catra-fg-smoke-test: FSR 3 FG playback smoke (SPRINT_04 subtask 04)\n");

    bool expectUnavailable = false;
    for (int i = 1; i < argc; ++i)
    {
        if (std::strcmp(argv[i], "--expect-unavailable") == 0)
        {
            expectUnavailable = true;
        }
    }

    catra_set_log_callback(&LogSink);

    // --- 1. window + D3D11 device + bridge init ----------------------------
    HWND hwnd = CreateTestWindow(640, 360);
    if (hwnd == nullptr)
    {
        return 1;
    }

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> ctx11;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                                   D3D11_SDK_VERSION, device.GetAddressOf(),
                                   &fl, ctx11.GetAddressOf());
    if (FAILED(hr))
    {
        fprintf(stderr, "SKIP D3D11CreateDevice hr=0x%08lX (no D3D11 GPU)\n",
                static_cast<unsigned long>(hr));
        return 0;
    }

    if (catra_init(device.Get()) != CATRA_OK)
    {
        fprintf(stderr, "FAIL catra_init\n");
        return 1;
    }

    // --- 2. availability gate ----------------------------------------------
    const int available = catra_is_fg_available();
    printf("catra_is_fg_available() = %d\n", available);

    if (!available)
    {
        if (expectUnavailable)
        {
            // Must ALSO fail the create cleanly (no crash).
            int ctx = -1;
            const int crc = catra_fg_create(hwnd, 640, 360, 30.0, &ctx);
            printf("catra_fg_create on unavailable runtime -> rc=%d (ctx=%d)\n",
                   crc, ctx);
            if (crc >= CATRA_OK)
            {
                fprintf(stderr,
                        "FAIL expected catra_fg_create to fail when unavailable\n");
                catra_shutdown();
                return 1;
            }
            catra_shutdown();
            printf("PASS (--expect-unavailable: clean failure, no crash)\n");
            return 0;
        }
        printf("SKIP FFX runtime / compatible GPU not present — "
               "graceful skip (CI-safe)\n");
        catra_shutdown();
        return 0;
    }

    if (expectUnavailable)
    {
        fprintf(stderr,
                "FAIL --expect-unavailable but catra_is_fg_available()==1 "
                "(the 8 FFX DLLs are co-located; run from a dir without them)\n");
        catra_shutdown();
        return 1;
    }

    // --- 3. create FG context on the HWND ----------------------------------
    int ctx = -1;
    int rc = catra_fg_create(hwnd, 640, 360, 30.0, &ctx);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "FAIL catra_fg_create rc=%d\n", rc);
        catra_shutdown();
        return 1;
    }
    printf("catra_fg_create OK (ctx=%d)\n", ctx);
    catra::FgSetPresentObserver(ctx, &PresentObserver, nullptr);

    // --- 4. 90 presents at ~30 fps (3 s) -----------------------------------
    ComPtr<ID3D11Texture2D> frame = MakeFrame(device.Get(), 640, 360, 0);
    if (frame == nullptr)
    {
        catra_shutdown();
        return 1;
    }

    const int kFrames = 90;
    for (int i = 0; i < kFrames; ++i)
    {
        PumpMessages();
        // Vary the frame content slightly so pacing is observable.
        const int prc = catra_fg_present(ctx, frame.Get(), 640, 360);
        if (prc != CATRA_OK)
        {
            fprintf(stderr, "FAIL catra_fg_present #%d rc=%d\n", i, prc);
            catra_shutdown();
            return 1;
        }
        Sleep(33); // ~30 fps cadence (the caller's clock drives submission)
        if ((i % 30) == 29)
        {
            printf("progress: %d presents submitted\n", i + 1);
            fflush(stdout);
        }
    }
    printf("present loop done\n");
    fflush(stdout);

    const uint64_t presents = g_presents.load();
    const uint64_t generated = g_generated.load();
    const double ratio = presents > 0
        ? static_cast<double>(presents) / static_cast<double>(kFrames)
        : 0.0;
    printf("presents=%llu generated=%llu ratio=%.2f (submitted=%d)\n",
           static_cast<unsigned long long>(presents),
           static_cast<unsigned long long>(generated), ratio, kFrames);
    if (presents == 0)
    {
        fprintf(stderr, "FAIL no presents observed (observer not fired)\n");
        catra_shutdown();
        return 1;
    }
    // Acceptance: >= 1.5x the submitted frames (expected ~2x at 60 Hz). On a
    // display slower than ~45 Hz this legitimately fails; the ratio is printed
    // for diagnosis.
    if (presents < static_cast<uint64_t>(kFrames * 3 / 2))
    {
        fprintf(stderr,
                "FAIL presents %llu < 1.5x submitted (%d) — display < ~45 Hz?\n",
                static_cast<unsigned long long>(presents), kFrames);
        catra_shutdown();
        return 1;
    }

    // --- 5. resize to 800x450 + 10 presents at the new size ----------------
    printf("resize start\n");
    fflush(stdout);
    rc = catra_fg_resize(ctx, 800, 450);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "FAIL catra_fg_resize rc=%d\n", rc);
        catra_shutdown();
        return 1;
    }
    printf("catra_fg_resize(800x450) OK\n");

    ComPtr<ID3D11Texture2D> frameBig = MakeFrame(device.Get(), 800, 450, 64);
    if (frameBig == nullptr)
    {
        catra_shutdown();
        return 1;
    }
    for (int i = 0; i < 10; ++i)
    {
        PumpMessages();
        const int prc = catra_fg_present(ctx, frameBig.Get(), 800, 450);
        if (prc != CATRA_OK)
        {
            fprintf(stderr, "FAIL catra_fg_present (post-resize) #%d rc=%d\n", i, prc);
            catra_shutdown();
            return 1;
        }
        Sleep(16);
    }

    // No-op resize (same size) must be CATRA_OK.
    printf("no-op resize start\n");
    fflush(stdout);
    rc = catra_fg_resize(ctx, 800, 450);
    if (rc != CATRA_OK)
    {
        fprintf(stderr, "FAIL no-op catra_fg_resize rc=%d\n", rc);
        catra_shutdown();
        return 1;
    }

    // Invalid-arg present must be rejected cleanly.
    rc = catra_fg_present(ctx, nullptr, 100, 100);
    if (rc != CATRA_ERR_INVALID_ARG)
    {
        fprintf(stderr, "FAIL invalid-arg present rc=%d (expected %d)\n",
                rc, CATRA_ERR_INVALID_ARG);
        catra_shutdown();
        return 1;
    }
    rc = catra_fg_present(ctx, frameBig.Get(), 0, 450);
    if (rc != CATRA_ERR_INVALID_ARG)
    {
        fprintf(stderr, "FAIL zero-dim present rc=%d (expected %d)\n",
                rc, CATRA_ERR_INVALID_ARG);
        catra_shutdown();
        return 1;
    }
    printf("invalid-arg presents rejected OK\n");

    // --- 6. destroy + 3x create/destroy churn (GDI stable) ------------------
    catra_fg_destroy(ctx);
    printf("catra_fg_destroy OK\n");

    const DWORD gdiBefore = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    for (int cycle = 0; cycle < 3; ++cycle)
    {
        int c2 = -1;
        rc = catra_fg_create(hwnd, 640, 360, 30.0, &c2);
        if (rc != CATRA_OK)
        {
            fprintf(stderr, "FAIL churn create #%d rc=%d\n", cycle, rc);
            catra_shutdown();
            return 1;
        }
        PumpMessages();
        const int prc = catra_fg_present(c2, frame.Get(), 640, 360);
        if (prc != CATRA_OK)
        {
            fprintf(stderr, "FAIL churn present #%d rc=%d\n", cycle, prc);
            catra_shutdown();
            return 1;
        }
        Sleep(33);
        catra_fg_destroy(c2);
    }
    const DWORD gdiAfter = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    printf("GDI objects churn: before=%lu after=%lu\n", gdiBefore, gdiAfter);
    if (gdiAfter > gdiBefore + 8) // small tolerance for unrelated churn
    {
        fprintf(stderr,
                "FAIL GDI object leak across churn: %lu -> %lu\n",
                gdiBefore, gdiAfter);
        catra_shutdown();
        return 1;
    }

    // Unknown-handle destroy is a no-op (must not crash).
    catra_fg_destroy(9999);

    // --- 7. shutdown -------------------------------------------------------
    catra_shutdown();
    printf("PASS\n");
    return 0;
}

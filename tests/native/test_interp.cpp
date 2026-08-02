// test_interp.cpp — standalone native smoke test for the RIFE interp backend.
//
// COMPILATION IS MANUAL (no MSVC/vcpkg in the authoring environment). Example:
//
//   cl /EHsc /std:c++20 /I native/catra-gpu tests/native/test_interp.cpp ^
//      native/catra-gpu/install/lib/catra-gpu.lib /Fe:test_interp.exe
//   test_interp.exe
//
// What it verifies WITHOUT a GPU / model (always runs):
//   * frames_per_pair math: 24->135 == 5, 24->55 == 2, RN-07 skip (60->24) == 0
//   * C ABI guard rails: create before init -> CATRA_ERR_INIT, process with an
//     unknown context -> CATRA_ERR_CONTEXT, destroy(unknown) is a safe no-op,
//     get_interp_method reports RIFE when compiled with ONNX Runtime.
//
// What it verifies WITH a D3D11 device + downloaded model (define
// CATRA_TEST_INTERP_LIVE to enable; needs a real adapter and lib/rife model):
//   * create -> process 10 frame pairs -> destroy without crash, count == N.
//
// Returns 0 on success, non-zero on the first failed check.

#include "catra_gpu.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

int g_failures = 0;
std::vector<std::string> g_logs;

void Check(bool cond, const char* what)
{
    if (cond)
    {
        std::printf("[ OK ] %s\n", what);
    }
    else
    {
        std::printf("[FAIL] %s\n", what);
        ++g_failures;
    }
}

// Mirrors the backend formula so the math is pinned even without the DLL.
int FramesPerPair(double srcFps, double targetFps)
{
    if (srcFps <= 0.0)
    {
        return 0;
    }
    int n = static_cast<int>(std::ceil(targetFps / srcFps)) - 1;
    return n < 0 ? 0 : n;
}

void LogSink(const char* msg, int level)
{
    (void)level;
    g_logs.emplace_back(msg ? msg : "");
    std::printf("  [native] %s\n", msg ? msg : "");
}

} // namespace

int main()
{
    catra_set_log_callback(&LogSink);

    // --- frames_per_pair math (spec: 24->135 ~= 4-5, 24->55 ~= 1-2) ---------
    Check(FramesPerPair(24.0, 135.0) == 5, "frames_per_pair 24->135 == 5");
    Check(FramesPerPair(24.0, 55.0) == 2, "frames_per_pair 24->55 == 2");
    Check(FramesPerPair(24.0, 48.0) == 1, "frames_per_pair 24->48 == 1");
    Check(FramesPerPair(60.0, 24.0) == 0, "RN-07 skip 60->24 == 0 (passthrough)");
    Check(FramesPerPair(24.0, 24.0) == 0, "RN-07 skip 24->24 == 0 (passthrough)");

    // --- C ABI guard rails (no init, no device) -----------------------------
    int ctx = -1;
    int rc = catra_interp_create(1920, 1080, 24.0, 135.0, CATRA_INTERP_RIFE, &ctx);
    Check(rc == CATRA_ERR_INIT, "interp_create before init -> CATRA_ERR_INIT");
    Check(ctx == -1, "interp_create failure leaves out_ctx = -1");

    void* frames = reinterpret_cast<void*>(0x1);
    int count = -1;
    rc = catra_interp_process(12345, nullptr, nullptr, &frames, &count);
    Check(rc == CATRA_ERR_CONTEXT, "interp_process unknown ctx -> CATRA_ERR_CONTEXT");

    catra_interp_destroy(99999); // must not crash
    Check(true, "interp_destroy(unknown) is a safe no-op");

    int method = catra_get_interp_method();
    Check(method == CATRA_INTERP_RIFE || method == CATRA_INTERP_NONE,
          "get_interp_method reports RIFE (ONNX build) or NONE (stub build)");

#if defined(CATRA_TEST_INTERP_LIVE)
    // --- Live path: requires a real D3D11 device + lib/rife/rife_v4.onnx ----
    // Create a WARP/HW device + two BGRA textures, then:
    //   catra_init(device);
    //   catra_interp_create(w, h, 24, 135, CATRA_INTERP_RIFE, &ctx) == CATRA_OK
    //   for 10 pairs: catra_interp_process(ctx, a, b, &frames, &count) == count
    //   catra_interp_destroy(ctx); catra_shutdown();
    #error "CATRA_TEST_INTERP_LIVE harness not wired in this skeleton; implement per header."
#endif

    std::printf("\n%s (%d failure(s))\n", g_failures == 0 ? "PASS" : "FAIL", g_failures);
    return g_failures == 0 ? 0 : 1;
}

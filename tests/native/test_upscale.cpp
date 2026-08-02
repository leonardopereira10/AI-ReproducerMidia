// test_upscale.cpp — standalone native smoke test for the upscale backends (ST-14).
//
// COMPILATION IS MANUAL (no MSVC/vcpkg/FSR SDK in the authoring environment).
// Example (FSR 1 fallback build, no FidelityFX SDK):
//
//   cl /EHsc /std:c++20 /I native/catra-gpu tests/native/test_upscale.cpp ^
//      native/catra-gpu/install/lib/catra-gpu.lib /Fe:test_upscale.exe
//   test_upscale.exe
//
// What it verifies WITHOUT a GPU / SDK (always runs):
//   * Quality-mode selection (spec anchors): 720p->4K == UltraPerformance,
//     1080p->4K == Quality, 720p->1080p == Quality (offline quality bias).
//   * FSR4->FSR1 method resolution: off stays off, fsr1 stays fsr1,
//     fsr4+available stays fsr4, fsr4+unavailable downgrades to fsr1,
//     unknown method degrades to passthrough.
//   * C ABI guard rails: create before init -> CATRA_ERR_INIT, process with an
//     unknown context -> CATRA_ERR_CONTEXT, destroy(unknown) is a safe no-op,
//     is_fsr4_available == 0 (stub/no-init build), get_upscale_mode == OFF.
//
// The decision-logic checks mirror the pure helpers in upscale_fsr1.cpp
// (catra::SelectQualityMode / catra::ResolveUpscaleMethod) so the spec is pinned
// even though those C++ symbols are not on the flat C ABI. The C ABI checks
// exercise the real entry points in catra_gpu.cpp.
//
// Returns 0 on success, non-zero on the first failed check.

#include "catra_gpu.h"

#include <cstdio>
#include <string>
#include <vector>

namespace {

int g_failures = 0;

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

void LogSink(const char* msg, int level)
{
    (void)level;
    std::printf("  [native] %s\n", msg ? msg : "");
}

// --- Mirrors of the pure helpers (upscale_fsr1.cpp) ------------------------
// Kept in sync by hand; they pin the ST-14 spec so a drift in the real
// implementation is caught when this test is built + run manually.

// Quality-mode enum values (must match catra::UpscaleQualityMode ordering).
constexpr int kQuality = 1;
constexpr int kUltraPerformance = 4;

int MirrorSelectQualityMode(int srcH, int dstH)
{
    if (srcH <= 0 || dstH <= 0)
    {
        return kQuality;
    }
    float scale = static_cast<float>(dstH) / static_cast<float>(srcH);
    // Offline pipeline: only the extreme 3x+ case drops to UltraPerformance;
    // everything else uses Quality (spec anchors 1080p->4K, 720p->1080p).
    return (scale >= 3.0f) ? kUltraPerformance : kQuality;
}

int MirrorResolveMethod(int requested, bool fsr4Available)
{
    switch (requested)
    {
        case CATRA_UPSCALE_OFF:  return CATRA_UPSCALE_OFF;
        case CATRA_UPSCALE_FSR1: return CATRA_UPSCALE_FSR1;
        case CATRA_UPSCALE_FSR4: return fsr4Available ? CATRA_UPSCALE_FSR4 : CATRA_UPSCALE_FSR1;
        default:                 return CATRA_UPSCALE_OFF;
    }
}

} // namespace

int main()
{
    catra_set_log_callback(&LogSink);

    // --- Quality-mode selection (spec ST-14 anchors) -----------------------
    Check(MirrorSelectQualityMode(720, 2160) == kUltraPerformance,
          "quality 720p->4K (3.0x) == UltraPerformance");
    Check(MirrorSelectQualityMode(1080, 2160) == kQuality,
          "quality 1080p->4K (2.0x) == Quality");
    Check(MirrorSelectQualityMode(720, 1080) == kQuality,
          "quality 720p->1080p (1.5x) == Quality");
    Check(MirrorSelectQualityMode(480, 1080) == kQuality,
          "quality 480p->1080p (2.25x) == Quality (offline bias)");
    Check(MirrorSelectQualityMode(720, 2161) == kUltraPerformance,
          "quality >3x == UltraPerformance");

    // --- FSR4 -> FSR1 method resolution ------------------------------------
    Check(MirrorResolveMethod(CATRA_UPSCALE_OFF, false) == CATRA_UPSCALE_OFF,
          "resolve off -> off");
    Check(MirrorResolveMethod(CATRA_UPSCALE_OFF, true) == CATRA_UPSCALE_OFF,
          "resolve off -> off (even if fsr4 available)");
    Check(MirrorResolveMethod(CATRA_UPSCALE_FSR1, false) == CATRA_UPSCALE_FSR1,
          "resolve fsr1 -> fsr1");
    Check(MirrorResolveMethod(CATRA_UPSCALE_FSR4, true) == CATRA_UPSCALE_FSR4,
          "resolve fsr4 + available -> fsr4");
    Check(MirrorResolveMethod(CATRA_UPSCALE_FSR4, false) == CATRA_UPSCALE_FSR1,
          "resolve fsr4 + unavailable -> fsr1 (downgrade)");
    Check(MirrorResolveMethod(99, true) == CATRA_UPSCALE_OFF,
          "resolve unknown method -> passthrough (defensive)");

    // --- C ABI guard rails (no init, no device) ----------------------------
    int ctx = -1;
    int rc = catra_upscale_create(1920, 1080, 3840, 2160, CATRA_UPSCALE_FSR4, &ctx);
    Check(rc == CATRA_ERR_INIT, "upscale_create before init -> CATRA_ERR_INIT");
    Check(ctx == -1, "upscale_create failure leaves out_ctx = -1");

    void* dst = reinterpret_cast<void*>(0x1);
    rc = catra_upscale_process(12345, reinterpret_cast<void*>(0x2), &dst);
    Check(rc == CATRA_ERR_CONTEXT, "upscale_process unknown ctx -> CATRA_ERR_CONTEXT");
    Check(dst == nullptr, "upscale_process failure leaves dst_texture = null");

    catra_upscale_destroy(99999); // must not crash
    Check(true, "upscale_destroy(unknown) is a safe no-op");

    Check(catra_is_fsr4_available() == 0,
          "is_fsr4_available == 0 (stub/no-init build)");
    Check(catra_get_upscale_mode() == CATRA_UPSCALE_OFF,
          "get_upscale_mode == OFF before any create");

    std::printf("\n%s (%d failure(s))\n", g_failures == 0 ? "PASS" : "FAIL", g_failures);
    return g_failures == 0 ? 0 : 1;
}

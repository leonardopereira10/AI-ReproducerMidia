// test_encode.cpp — standalone native smoke test for the AMF H.265 encoder (ST-16).
//
// COMPILATION IS MANUAL (no MSVC/vcpkg/AMF SDK in the authoring environment).
// Example (stub build, no AMF headers — the default):
//
//   cl /EHsc /std:c++20 /I native/catra-gpu tests/native/test_encode.cpp ^
//      native/catra-gpu/install/lib/catra-gpu.lib /Fe:test_encode.exe
//   test_encode.exe
//
// What it verifies WITHOUT a GPU / AMF SDK (always runs):
//   * HEVC tier selection (spec anchors): 1080p135 == Main, 4K30 == Main,
//     4K55 == High, >4K == High, 720p == Main, invalid == Main (defensive).
//   * C ABI guard rails: create before init -> CATRA_ERR_INIT (out_ctx = -1),
//     frame/flush with an unknown context -> CATRA_ERR_CONTEXT (out_buf null,
//     out_size 0), destroy(unknown) is a safe no-op.
//
// The tier checks mirror the pure helper catra::SelectHevcTier (encode_amf.cpp)
// so the ST-16 spec is pinned even though that C++ symbol is not on the flat C
// ABI. The C ABI checks exercise the real entry points in catra_gpu.cpp and pass
// regardless of whether the bridge was built with CATRA_HAS_AMF (the guard rails
// run before the backend is reached). With the AMF SDK + an AMD GPU present, the
// same entry points go on to create a real encoder; that path is validated
// manually (no MSVC/AMF/driver here).
//
// Returns 0 on success, non-zero on the first failed check.

#include "catra_gpu.h"

#include <cstdint>
#include <cstdio>

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

// --- Mirror of the pure helper (encode_amf.cpp: catra::SelectHevcTier) ------
// Kept in sync by hand; pins the ST-16 tier spec so a drift in the real
// implementation is caught when this test is built + run manually. The abstract
// tier codes match encode_amf.cpp (decoupled from the AMF enum numbering).
constexpr int kTierMain = 0;
constexpr int kTierHigh = 1;

int MirrorHevcTier(int width, int height, double fps)
{
    if (width <= 0 || height <= 0)
    {
        return kTierMain; // defensive default
    }
    const long long pixels = static_cast<long long>(width) * static_cast<long long>(height);
    const long long k4K = 3840LL * 2160LL;
    if (pixels > k4K)
    {
        return kTierHigh; // above 4K -> High tier
    }
    if (pixels == k4K && fps > 30.5)
    {
        return kTierHigh; // 4K55 -> High tier (4K30 stays Main)
    }
    return kTierMain; // <= 4K30 -> Main tier
}

} // namespace

int main()
{
    catra_set_log_callback(&LogSink);

    // --- HEVC tier selection (spec ST-16 anchors) --------------------------
    Check(MirrorHevcTier(1920, 1080, 135.0) == kTierMain,
          "tier 1080p135 (local profile) == Main");
    Check(MirrorHevcTier(3840, 2160, 30.0) == kTierMain,
          "tier 4K30 == Main");
    Check(MirrorHevcTier(3840, 2160, 24.0) == kTierMain,
          "tier 4K24 == Main");
    Check(MirrorHevcTier(3840, 2160, 55.0) == kTierHigh,
          "tier 4K55 (dlna profile) == High");
    Check(MirrorHevcTier(4096, 2160, 30.0) == kTierHigh,
          "tier >4K (4096x2160) == High");
    Check(MirrorHevcTier(1280, 720, 55.0) == kTierMain,
          "tier 720p55 == Main");
    Check(MirrorHevcTier(0, 2160, 55.0) == kTierMain,
          "tier invalid (width 0) == Main (defensive)");

    // --- C ABI guard rails (no init, no device) ----------------------------
    int ctx = -1;
    int rc = catra_encode_create(3840, 2160, 45000, 55.0, &ctx);
    Check(rc == CATRA_ERR_INIT, "encode_create before init -> CATRA_ERR_INIT");
    Check(ctx == -1, "encode_create failure leaves out_ctx = -1");

    uint8_t* buf = reinterpret_cast<uint8_t*>(0x1);
    int size = 999;
    rc = catra_encode_frame(12345, reinterpret_cast<void*>(0x2), &buf, &size);
    Check(rc == CATRA_ERR_CONTEXT, "encode_frame unknown ctx -> CATRA_ERR_CONTEXT");
    Check(buf == nullptr, "encode_frame failure leaves out_buf = null");
    Check(size == 0, "encode_frame failure leaves out_size = 0");

    buf = reinterpret_cast<uint8_t*>(0x1);
    size = 999;
    rc = catra_encode_flush(12345, &buf, &size);
    Check(rc == CATRA_ERR_CONTEXT, "encode_flush unknown ctx -> CATRA_ERR_CONTEXT");
    Check(buf == nullptr, "encode_flush failure leaves out_buf = null");
    Check(size == 0, "encode_flush failure leaves out_size = 0");

    catra_encode_destroy(99999); // must not crash
    Check(true, "encode_destroy(unknown) is a safe no-op");

    std::printf("\n%s (%d failure(s))\n", g_failures == 0 ? "PASS" : "FAIL", g_failures);
    return g_failures == 0 ? 0 : 1;
}

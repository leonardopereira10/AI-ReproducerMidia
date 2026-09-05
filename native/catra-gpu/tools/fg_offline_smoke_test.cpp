// fg_offline_smoke_test.cpp — SPIKE v2: FSR 3/4 Frame Generation OFFLINE (no
// playback swapchain) feasibility POC (subtask 03, Story 03). NOT shipped /
// NOT installed. Disposable evidence-gathering tool for the GO/NO-GO decision.
//
// Build: target catra-fg-offline-test in CMakeLists.txt (compiles ONLY
// ffx_runtime.cpp into the exe — no bridge ABI, no catra_init; the tool owns
// its own D3D12 device/queue. POST_BUILD copies the 8 FFX runtime DLLs).
//
// Unknowns resolved here (evidence written to docs/fsr/fg_offline_spike.md):
//   U1 — ffxConfigureDescFrameGeneration with swapChain = nullptr
//        (fallback probe: hidden-HWND dummy swapchain never presented)
//   U2 — does an offscreen FG dispatch WITHOUT a playback swapchain produce a
//        genuinely INTERPOLATED frame? Probed in several configurations
//        (with/without the Prepare dispatch, +/- motion-vector sign, BGRA vs
//        RGBA backbuffer format) because the reference sample ALWAYS pairs
//        Prepare(depth+MV+frameTimeDelta) with the FG dispatch.
//   U3 — D3D12 resource-state scheme for outputs[] (UNORDERED_ACCESS vs
//        PIXEL_COMPUTE_READ declaration), probed empirically
//   U4 — channel/format fidelity of the returned frame (R<->B swap detection)
//   U5 — frameID contract (+1 exactly per dispatch; gap probe)
//   U6 — cost per generated frame at 1920x1080 (timed run)
//
// Usage:
//   catra-fg-offline-test.exe [--width 640] [--height 360] [--frames 12]
//                             [--shift 16] [--phase noprep-bgra] [--no-u6]
// Exit codes: 0 = GO / graceful SKIP (no FFX runtime or no DX12 GPU),
//             1 = NO-GO or hard failure.
//
// ---------------------------------------------------------------------------
// VALIDATION METHOD (v2 — unambiguous, flow-friendly, quantified)
//
// v1 used a triangle wave with period 64 and an 8 px scroll. That pattern is
// pathological for this measurement: a triangle wave is an EVEN function, so a
// horizontal mirror is indistinguishable from a translation, and any offset
// that is a multiple of half the period aliases onto another legal offset. The
// v1 readback matched "current source frame, R<->B swapped, rolled by exactly
// 32 px (= half the pattern period)" with ZERO error at both 640x360 and
// 1920x1080 — i.e. the discriminator could not separate "passthrough copy"
// from "mirrored/rolled interpolation artefact".
//
// v2 pattern (continuous, band-limited, asymmetric, unique per frame):
//   R = 40 + 100 * frac((x + off) / 256)          sawtooth ramp (asymmetric:
//                                                 mirror != translation, and
//                                                 off is unique mod 256)
//   G = 55 + 160 * gauss(x - (blobX0 - off), y - h/2, sigma = 14)
//                                                 single moving marker whose
//                                                 centre encodes `off` with
//                                                 sub-pixel accuracy
//   B = 40 + 60 * (y / h) + 20 * frac((x + off) / 512)
//                                                 vertical gradient (detects
//                                                 flips / vertical rolls)
// The whole scene translates by `shift` px per presented frame, so the true
// in-between frame sits at t = 0.5:
//   t = (off - off_output) / shift
//   t = 0.0 -> output == current present  (passthrough, no generation)
//   t = 0.5 -> genuine interpolation      (GO evidence)
//   t = 1.0 -> output == previous present (stale history / flow broken)
//
// `off_output` is estimated two independent ways per dispatch:
//   (a) blob argmax with parabolic sub-pixel refinement on the peak channel;
//   (b) exhaustive normalised-cross-correlation (NCC) of the readback against
//       the analytic pattern over t in [-0.75, 1.75] (step 0.05) x {channel
//       identity, R<->B swap} x {normal, x-mirrored} = 204 hypotheses. NCC is
//       invariant to affine intensity changes (gamma / transfer function), so
//       the estimate survives the sRGB round-trip the FG applies.
// The first 2 dispatches of every phase are warm-up (no history yet).

#include "../ffx_runtime.h" // defines _WINDOWS before the FFX headers

#include <ffx_framegeneration.h>
#include <dx12/ffx_api_dx12.h> // ffxApiGetResourceDX12

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;
using catra::ffx::FfxRuntime;

namespace catra {
// catra::BackendLog shim — ffx_runtime.cpp forward-declares it; the real
// definition lives in catra_gpu.cpp which this tool deliberately does NOT
// link (spike isolation: no bridge ABI, no catra_init).
void BackendLog(int level, const char* fmt, ...)
{
    static const char* names[] = {"DBG", "INF", "WRN", "ERR"};
    const int idx = (level >= 0 && level <= 3) ? level : 3;
    char buffer[2048];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(buffer, sizeof(buffer), fmt, args);
    va_end(args);
    printf("[ffx %s] %s\n", names[idx], buffer);
    fflush(stdout);
}
} // namespace catra

namespace {

constexpr DWORD kFenceWaitTimeoutMs = 10000; // bounded: never INFINITE
constexpr float kSigma = 14.0f;              // blob radius (px)
constexpr float kRampA = 256.0f;             // R sawtooth period (px)
constexpr float kRampB = 512.0f;             // B sawtooth period (px)
constexpr int kSampleCols = 96;              // NCC sample grid
constexpr int kSampleRows = 54;

int g_shift = 16; // scene translation per presented frame (px)

inline float Frac(float v)
{
    return v - std::floor(v);
}

// Analytic pattern in SEMANTIC channels (R,G,B), 0..255.
void PatternRGB(float x, float y, float off, int w, int h, float blobX0, float* out)
{
    const float bx = blobX0 - off;
    const float dx = x - bx;
    const float dy = y - 0.5f * static_cast<float>(h);
    const float g = std::exp(-(dx * dx + dy * dy) / (2.0f * kSigma * kSigma));
    out[0] = 40.0f + 100.0f * Frac((x + off) / kRampA);
    out[1] = 55.0f + 160.0f * g;
    out[2] = 40.0f + 60.0f * (y / static_cast<float>(h)) + 20.0f * Frac((x + off) / kRampB);
}

struct FrameSpec
{
    int w = 0, h = 0;
    DXGI_FORMAT fmt = DXGI_FORMAT_B8G8R8A8_UNORM;
    float off = 0.0f;
    float blobX0 = 0.0f;
};

inline bool IsBgra(DXGI_FORMAT f)
{
    return f == DXGI_FORMAT_B8G8R8A8_UNORM;
}

inline uint8_t Clamp8(float v)
{
    const int i = static_cast<int>(std::lround(v));
    return static_cast<uint8_t>(i < 0 ? 0 : (i > 255 ? 255 : i));
}

// Packs semantic RGB into the memory byte order of `fmt`.
inline uint32_t PackPixel(DXGI_FORMAT fmt, float r, float g, float b)
{
    const uint32_t R = Clamp8(r), G = Clamp8(g), B = Clamp8(b);
    if (IsBgra(fmt)) return 0xFF000000u | (R << 16) | (G << 8) | B;
    return 0xFF000000u | (B << 16) | (G << 8) | R;
}

// Inverse of PackPixel.
inline void UnpackPixel(DXGI_FORMAT fmt, uint32_t p, float* rgb)
{
    if (IsBgra(fmt))
    {
        rgb[0] = static_cast<float>((p >> 16) & 0xFF);
        rgb[1] = static_cast<float>((p >> 8) & 0xFF);
        rgb[2] = static_cast<float>(p & 0xFF);
    }
    else
    {
        rgb[0] = static_cast<float>(p & 0xFF);
        rgb[1] = static_cast<float>((p >> 8) & 0xFF);
        rgb[2] = static_cast<float>((p >> 16) & 0xFF);
    }
}

// Separable fill (Gaussian is separable; ramps are y-independent).
void FillFrame(std::vector<uint32_t>& px, const FrameSpec& s)
{
    const int w = s.w, h = s.h;
    px.resize(static_cast<size_t>(w) * static_cast<size_t>(h));
    std::vector<float> rampA(static_cast<size_t>(w)), rampB(static_cast<size_t>(w)),
        gx(static_cast<size_t>(w));
    const float bx = s.blobX0 - s.off;
    for (int x = 0; x < w; ++x)
    {
        const float xf = static_cast<float>(x);
        rampA[static_cast<size_t>(x)] = 40.0f + 100.0f * Frac((xf + s.off) / kRampA);
        rampB[static_cast<size_t>(x)] = 20.0f * Frac((xf + s.off) / kRampB);
        const float dx = xf - bx;
        gx[static_cast<size_t>(x)] = std::exp(-(dx * dx) / (2.0f * kSigma * kSigma));
    }
    for (int y = 0; y < h; ++y)
    {
        const float dy = static_cast<float>(y) - 0.5f * static_cast<float>(h);
        const float gy = std::exp(-(dy * dy) / (2.0f * kSigma * kSigma));
        const float vBase = 40.0f + 60.0f * (static_cast<float>(y) / static_cast<float>(h));
        uint32_t* dst = px.data() + static_cast<size_t>(y) * static_cast<size_t>(w);
        for (int x = 0; x < w; ++x)
        {
            dst[static_cast<size_t>(x)] = PackPixel(s.fmt, rampA[static_cast<size_t>(x)],
                                                    55.0f + 160.0f * gx[static_cast<size_t>(x)] * gy,
                                                    vBase + rampB[static_cast<size_t>(x)]);
        }
    }
}

// ---------------------------------------------------------------------------
// Analysis
// ---------------------------------------------------------------------------

struct SampleGrid
{
    std::vector<int> xs, ys; // kSampleCols * kSampleRows positions
};

SampleGrid MakeGrid(int w, int h)
{
    SampleGrid g;
    g.xs.reserve(static_cast<size_t>(kSampleCols) * kSampleRows);
    g.ys.reserve(static_cast<size_t>(kSampleCols) * kSampleRows);
    for (int iy = 0; iy < kSampleRows; ++iy)
    {
        const int y = static_cast<int>((static_cast<int64_t>(h) * (iy * 2 + 1)) / (kSampleRows * 2));
        for (int ix = 0; ix < kSampleCols; ++ix)
        {
            const int x = static_cast<int>((static_cast<int64_t>(w) * (ix * 2 + 1)) / (kSampleCols * 2));
            g.xs.push_back(x);
            g.ys.push_back(y);
        }
    }
    return g;
}

struct Analysis
{
    double nonBlackFrac = 0.0;
    // best NCC hypothesis
    double bestT = 0.0, bestNcc = -2.0, bestMad = 1e9;
    int bestSwap = 0, bestMirror = 0;
    // NCC at the three semantic hypotheses, under the best mapping
    double nccPassthrough = -2.0; // t = 0
    double nccInterp = -2.0;      // t = 0.5
    double nccPrevCopy = -2.0;    // t = 1
    // blob-based estimate
    int blobChan = -1;
    double blobX = 0.0, blobT = 0.0, blobPeak = 0.0, blobContrast = 0.0;
    int blobPeaks = 0;
};

double NccAndMad(const std::vector<float>& a, const std::vector<float>& b, double* mad)
{
    const size_t n = a.size();
    if (n == 0) return -2.0;
    double sa = 0, sb = 0;
    for (size_t i = 0; i < n; ++i) { sa += a[i]; sb += b[i]; }
    const double ma = sa / static_cast<double>(n), mb = sb / static_cast<double>(n);
    double num = 0, da = 0, db = 0, ad = 0;
    for (size_t i = 0; i < n; ++i)
    {
        const double xa = a[i] - ma, xb = b[i] - mb;
        num += xa * xb; da += xa * xa; db += xb * xb;
        ad += std::fabs(static_cast<double>(a[i]) - static_cast<double>(b[i]));
    }
    *mad = ad / static_cast<double>(n);
    const double den = std::sqrt(da * db);
    return den > 1e-9 ? num / den : -2.0;
}

Analysis Analyze(const uint8_t* mapped, UINT pitch, const FrameSpec& s, const SampleGrid& grid,
                 float off)
{
    Analysis an;
    const int w = s.w, h = s.h;
    const size_t n = grid.xs.size();

    // Gather readback samples (semantic channels).
    std::vector<float> got(n * 3);
    int nonBlack = 0;
    for (size_t i = 0; i < n; ++i)
    {
        const uint8_t* p = mapped + static_cast<size_t>(grid.ys[i]) * pitch +
                           static_cast<size_t>(grid.xs[i]) * 4;
        const uint32_t raw = static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
                             (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
        float rgb[3];
        UnpackPixel(s.fmt, raw, rgb);
        got[i * 3 + 0] = rgb[0];
        got[i * 3 + 1] = rgb[1];
        got[i * 3 + 2] = rgb[2];
        if (p[0] > 8 || p[1] > 8 || p[2] > 8) ++nonBlack;
    }
    an.nonBlackFrac = static_cast<double>(nonBlack) / static_cast<double>(n);

    // --- blob estimate: mid-band row-average profile per channel -------------
    const int y0 = h / 3, y1 = (2 * h) / 3;
    std::vector<double> prof[3];
    for (int c = 0; c < 3; ++c) prof[c].assign(static_cast<size_t>(w), 0.0);
    int rows = 0;
    for (int y = y0; y < y1 && y < h; ++y)
    {
        const uint8_t* p = mapped + static_cast<size_t>(y) * pitch;
        for (int x = 0; x < w; ++x)
        {
            const uint32_t raw = static_cast<uint32_t>(p[static_cast<size_t>(x) * 4 + 0]) |
                                 (static_cast<uint32_t>(p[static_cast<size_t>(x) * 4 + 1]) << 8) |
                                 (static_cast<uint32_t>(p[static_cast<size_t>(x) * 4 + 2]) << 16);
            float rgb[3];
            UnpackPixel(s.fmt, raw, rgb);
            for (int c = 0; c < 3; ++c) prof[c][static_cast<size_t>(x)] += rgb[c];
        }
        ++rows;
    }
    if (rows > 0)
    {
        double bestContrast = -1.0;
        for (int c = 0; c < 3; ++c)
        {
            // remove the linear ramp component by high-passing with a wide box
            std::vector<double> sm(static_cast<size_t>(w), 0.0);
            const int R = 3 * static_cast<int>(kSigma);
            for (int x = 0; x < w; ++x)
            {
                double acc = 0; int cnt = 0;
                for (int k = -R; k <= R; k += 4)
                {
                    const int xx = x + k;
                    if (xx < 0 || xx >= w) continue;
                    acc += prof[c][static_cast<size_t>(xx)];
                    ++cnt;
                }
                sm[static_cast<size_t>(x)] = cnt > 0 ? acc / cnt : 0.0;
            }
            double mx = -1e9, mn = 1e9; int argmax = 0;
            std::vector<double> hp(static_cast<size_t>(w), 0.0);
            for (int x = 0; x < w; ++x)
            {
                hp[static_cast<size_t>(x)] = prof[c][static_cast<size_t>(x)] / rows - sm[static_cast<size_t>(x)];
                if (hp[static_cast<size_t>(x)] > mx) { mx = hp[static_cast<size_t>(x)]; argmax = x; }
                if (hp[static_cast<size_t>(x)] < mn) mn = hp[static_cast<size_t>(x)];
            }
            const double contrast = mx - mn;
            if (contrast > bestContrast)
            {
                bestContrast = contrast;
                an.blobChan = c;
                an.blobPeak = mx;
                an.blobContrast = contrast;
                // parabolic sub-pixel refinement
                double bx = argmax;
                if (argmax > 0 && argmax + 1 < w)
                {
                    const double a = hp[static_cast<size_t>(argmax - 1)];
                    const double b = hp[static_cast<size_t>(argmax)];
                    const double cc = hp[static_cast<size_t>(argmax + 1)];
                    const double den = (a - 2 * b + cc);
                    if (std::fabs(den) > 1e-9) bx = argmax + 0.5 * (a - cc) / den;
                }
                an.blobX = bx;
                // count local maxima above 50% of the peak (ghosting detector)
                int peaks = 0;
                for (int x = 2; x + 2 < w; ++x)
                {
                    const double v = hp[static_cast<size_t>(x)];
                    if (v > 0.5 * mx && v >= hp[static_cast<size_t>(x - 1)] && v >= hp[static_cast<size_t>(x - 2)] &&
                        v > hp[static_cast<size_t>(x + 1)] && v > hp[static_cast<size_t>(x + 2)])
                    {
                        ++peaks;
                    }
                }
                an.blobPeaks = peaks;
            }
        }
        const float blobX0 = s.blobX0;
        const double offOut = static_cast<double>(blobX0) - an.blobX;
        an.blobT = (static_cast<double>(off) - offOut) / static_cast<double>(g_shift);
    }

    // --- exhaustive NCC hypothesis search -----------------------------------
    std::vector<float> exp(n * 3);
    auto evalHypothesis = [&](int swap, int mirror, double t, double* mad) {
        const double offOut = static_cast<double>(off) - t * g_shift;
        for (size_t i = 0; i < n; ++i)
        {
            float x = static_cast<float>(grid.xs[i]);
            if (mirror) x = static_cast<float>(w - 1) - x;
            float rgb[3];
            PatternRGB(x, static_cast<float>(grid.ys[i]), static_cast<float>(offOut), w, h,
                       s.blobX0, rgb);
            float o[3] = {rgb[0], rgb[1], rgb[2]};
            if (swap) std::swap(o[0], o[2]);
            exp[i * 3 + 0] = o[0];
            exp[i * 3 + 1] = o[1];
            exp[i * 3 + 2] = o[2];
        }
        return NccAndMad(got, exp, mad);
    };

    for (int swap = 0; swap <= 1; ++swap)
    {
        for (int mirror = 0; mirror <= 1; ++mirror)
        {
            for (int it = -15; it <= 35; ++it) // t = -0.75 .. 1.75 step 0.05
            {
                const double t = it * 0.05;
                double mad = 0;
                const double ncc = evalHypothesis(swap, mirror, t, &mad);
                if (ncc > an.bestNcc)
                {
                    an.bestNcc = ncc; an.bestT = t; an.bestSwap = swap;
                    an.bestMirror = mirror; an.bestMad = mad;
                }
            }
        }
    }
    double mad = 0;
    an.nccPassthrough = evalHypothesis(an.bestSwap, an.bestMirror, 0.0, &mad);
    an.nccInterp = evalHypothesis(an.bestSwap, an.bestMirror, 0.5, &mad);
    an.nccPrevCopy = evalHypothesis(an.bestSwap, an.bestMirror, 1.0, &mad);
    return an;
}

// Writes a BGRA/RGBA framebuffer (from the staging map) as a 24-bit BMP.
bool WriteBmp(const char* path, const uint8_t* mapped, UINT pitch, int w, int h, DXGI_FORMAT fmt)
{
    FILE* f = std::fopen(path, "wb");
    if (f == nullptr) return false;
    const int rowBytes = w * 3;
    const int padBytes = (4 - (rowBytes % 4)) % 4;
    const uint32_t imgSize = static_cast<uint32_t>((rowBytes + padBytes) * h);
    const uint32_t fileSize = 54 + imgSize;
    uint8_t hdr[54] = {};
    hdr[0] = 'B'; hdr[1] = 'M';
    hdr[2] = static_cast<uint8_t>(fileSize & 0xFF); hdr[3] = static_cast<uint8_t>((fileSize >> 8) & 0xFF);
    hdr[4] = static_cast<uint8_t>((fileSize >> 16) & 0xFF); hdr[5] = static_cast<uint8_t>((fileSize >> 24) & 0xFF);
    hdr[10] = 54;
    hdr[14] = 40;
    hdr[18] = static_cast<uint8_t>(w & 0xFF); hdr[19] = static_cast<uint8_t>((w >> 8) & 0xFF);
    hdr[20] = static_cast<uint8_t>((w >> 16) & 0xFF); hdr[21] = static_cast<uint8_t>((w >> 24) & 0xFF);
    hdr[22] = static_cast<uint8_t>(h & 0xFF); hdr[23] = static_cast<uint8_t>((h >> 8) & 0xFF);
    hdr[24] = static_cast<uint8_t>((h >> 16) & 0xFF); hdr[25] = static_cast<uint8_t>((h >> 24) & 0xFF);
    hdr[26] = 1;
    hdr[28] = 24;
    hdr[34] = static_cast<uint8_t>(imgSize & 0xFF); hdr[35] = static_cast<uint8_t>((imgSize >> 8) & 0xFF);
    hdr[36] = static_cast<uint8_t>((imgSize >> 16) & 0xFF); hdr[37] = static_cast<uint8_t>((imgSize >> 24) & 0xFF);
    std::fwrite(hdr, 1, sizeof(hdr), f);
    std::vector<uint8_t> row(static_cast<size_t>(rowBytes + padBytes), 0);
    for (int y = h - 1; y >= 0; --y) // BMP is bottom-up
    {
        const uint8_t* src = mapped + static_cast<size_t>(y) * pitch;
        for (int x = 0; x < w; ++x)
        {
            float rgb[3];
            const uint32_t raw = static_cast<uint32_t>(src[static_cast<size_t>(x) * 4 + 0]) |
                                 (static_cast<uint32_t>(src[static_cast<size_t>(x) * 4 + 1]) << 8) |
                                 (static_cast<uint32_t>(src[static_cast<size_t>(x) * 4 + 2]) << 16);
            UnpackPixel(fmt, raw, rgb);
            row[static_cast<size_t>(x) * 3 + 0] = Clamp8(rgb[2]); // B
            row[static_cast<size_t>(x) * 3 + 1] = Clamp8(rgb[1]); // G
            row[static_cast<size_t>(x) * 3 + 2] = Clamp8(rgb[0]); // R
        }
        std::fwrite(row.data(), 1, row.size(), f);
    }
    std::fclose(f);
    return true;
}

uint16_t FloatToHalf(float f)
{
    uint32_t x = 0;
    std::memcpy(&x, &f, sizeof(x));
    const uint32_t sign = (x >> 16) & 0x8000u;
    const int32_t exp = static_cast<int32_t>((x >> 23) & 0xFF) - 127 + 15;
    const uint32_t mant = x & 0x7FFFFFu;
    if (((x >> 23) & 0xFF) == 0xFF) return static_cast<uint16_t>(sign | 0x7C00u | (mant ? 0x200u : 0u));
    if (exp >= 0x1F) return static_cast<uint16_t>(sign | 0x7C00u);
    if (exp <= 0)
    {
        if (exp < -10) return static_cast<uint16_t>(sign);
        const uint32_t m = mant | 0x800000u;
        const uint32_t sh = static_cast<uint32_t>(14 - exp);
        uint32_t half = m >> sh;
        if (m & (1u << (sh - 1))) ++half;
        return static_cast<uint16_t>(sign | half);
    }
    return static_cast<uint16_t>(sign | (static_cast<uint32_t>(exp) << 10) | (mant >> 13));
}

// ---------------------------------------------------------------------------
// D3D12 rig (owned entirely by the tool — no bridge interop)
// ---------------------------------------------------------------------------

struct Rig
{
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> cmdList;
    ComPtr<ID3D12Fence> fence;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;

    int w = 0, h = 0;
    DXGI_FORMAT fmt = DXGI_FORMAT_B8G8R8A8_UNORM;
    ComPtr<ID3D12Resource> src;        // presentColor source (updated per frame)
    void* srcMapped = nullptr;
    ComPtr<ID3D12Resource> srcUpload;  // persistently mapped upload buffer
    ComPtr<ID3D12Resource> gen;        // FG output (UAV-capable)
    ComPtr<ID3D12Resource> gen2;       // second FG output (numGeneratedFrames=2 probe)
    ComPtr<ID3D12Resource> staging;    // readback buffer
    ComPtr<ID3D12Resource> staging2;   // readback buffer for gen2
    UINT stagingPitch = 0;
    bool srcUploadedOnce = false;

    // Prepare inputs (created per phase, constant content)
    ComPtr<ID3D12Resource> depth;      // R32_FLOAT
    ComPtr<ID3D12Resource> mv;         // R16G16_FLOAT
    ComPtr<ID3D12Resource> depthUpload;
    ComPtr<ID3D12Resource> mvUpload;
    bool prepReady = false;

    bool CreatePlumbing()
    {
        HRESULT hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0,
                                       IID_PPV_ARGS(device.GetAddressOf()));
        if (FAILED(hr))
        {
            hr = D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0,
                                   IID_PPV_ARGS(device.GetAddressOf()));
        }
        if (FAILED(hr))
        {
            printf("SKIP D3D12CreateDevice hr=0x%08lX (no DX12 GPU)\n",
                   static_cast<unsigned long>(hr));
            return false;
        }

        D3D12_COMMAND_QUEUE_DESC qd = {};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        qd.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
        hr = device->CreateCommandQueue(&qd, IID_PPV_ARGS(queue.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL CreateCommandQueue hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

        hr = device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                                            IID_PPV_ARGS(allocator.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL CreateCommandAllocator hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

        hr = device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                                       allocator.Get(), nullptr,
                                       IID_PPV_ARGS(cmdList.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL CreateCommandList hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }
        cmdList->Close();

        hr = device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(fence.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL CreateFence hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

        fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (fenceEvent == nullptr) { printf("FAIL CreateEventW err=%lu\n", GetLastError()); return false; }
        return true;
    }

    bool WaitFence(UINT64 value)
    {
        if (fence->GetCompletedValue() >= value) return true;
        HRESULT hr = fence->SetEventOnCompletion(value, fenceEvent);
        if (FAILED(hr))
        {
            printf("FAIL SetEventOnCompletion hr=0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }
        const DWORD wr = WaitForSingleObject(fenceEvent, kFenceWaitTimeoutMs);
        if (wr != WAIT_OBJECT_0)
        {
            printf("FAIL fence wait result=%lu (timeout=%lu ms)\n",
                   static_cast<unsigned long>(wr),
                   static_cast<unsigned long>(kFenceWaitTimeoutMs));
            return false;
        }
        return true;
    }

    bool BeginList()
    {
        HRESULT hr = allocator->Reset();
        if (SUCCEEDED(hr)) hr = cmdList->Reset(allocator.Get(), nullptr);
        if (FAILED(hr))
        {
            printf("FAIL list reset hr=0x%08lX\n", static_cast<unsigned long>(hr));
            return false;
        }
        return true;
    }

    bool SubmitAndWait()
    {
        ID3D12CommandList* lists[] = {cmdList.Get()};
        queue->ExecuteCommandLists(1, lists);
        ++fenceValue;
        queue->Signal(fence.Get(), fenceValue);
        return WaitFence(fenceValue);
    }

    bool DeviceHealthy() const
    {
        return device->GetDeviceRemovedReason() == S_OK;
    }

    static void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* res,
                           D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        if (before == after) return;
        D3D12_RESOURCE_BARRIER b = {};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = res;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = before;
        b.Transition.StateAfter = after;
        list->ResourceBarrier(1, &b);
    }

    bool CreateUploadPair(DXGI_FORMAT format, size_t bytesPerPixel,
                          ComPtr<ID3D12Resource>& tex, ComPtr<ID3D12Resource>& upload,
                          const char* what)
    {
        D3D12_RESOURCE_DESC td = {};
        td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = static_cast<UINT>(w);
        td.Height = static_cast<UINT>(h);
        td.DepthOrArraySize = 1;
        td.MipLevels = 1;
        td.Format = format;
        td.SampleDesc.Count = 1;
        td.Flags = D3D12_RESOURCE_FLAG_NONE;
        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_DEFAULT;
        HRESULT hr = device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &td,
                                                     D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                     IID_PPV_ARGS(tex.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL %s texture hr=0x%08lX\n", what, static_cast<unsigned long>(hr)); return false; }

        const size_t frameBytes = static_cast<size_t>(w) * static_cast<size_t>(h) * bytesPerPixel;
        D3D12_RESOURCE_DESC ub = td;
        ub.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        ub.Width = frameBytes;
        ub.Height = 1;
        ub.Format = DXGI_FORMAT_UNKNOWN;
        ub.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        D3D12_HEAP_PROPERTIES up = {};
        up.Type = D3D12_HEAP_TYPE_UPLOAD;
        hr = device->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &ub,
                                             D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                             IID_PPV_ARGS(upload.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL %s upload hr=0x%08lX\n", what, static_cast<unsigned long>(hr)); return false; }
        return true;
    }

    // One-shot CPU -> upload buffer -> texture copy (constant content).
    bool UploadConstant(ComPtr<ID3D12Resource>& tex, ComPtr<ID3D12Resource>& upload,
                        const void* data, size_t bytesPerPixel, const char* what)
    {
        void* m = nullptr;
        HRESULT hr = upload->Map(0, nullptr, &m);
        if (FAILED(hr)) { printf("FAIL %s upload map hr=0x%08lX\n", what, static_cast<unsigned long>(hr)); return false; }
        std::memcpy(m, data, static_cast<size_t>(w) * static_cast<size_t>(h) * bytesPerPixel);
        upload->Unmap(0, nullptr);

        if (!BeginList()) return false;
        D3D12_TEXTURE_COPY_LOCATION dst = {};
        dst.pResource = tex.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
        srcLoc.pResource = upload.Get();
        srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        D3D12_RESOURCE_DESC td = tex->GetDesc();
        device->GetCopyableFootprints(&td, 0, 1, 0, &srcLoc.PlacedFootprint, nullptr, nullptr, nullptr);
        cmdList->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, nullptr);
        Transition(cmdList.Get(), tex.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                   D3D12_RESOURCE_STATE_ALL_SHADER_RESOURCE);
        hr = cmdList->Close();
        if (FAILED(hr)) { printf("FAIL %s list close hr=0x%08lX\n", what, static_cast<unsigned long>(hr)); return false; }
        if (!SubmitAndWait()) return false;
        return DeviceHealthy();
    }

    // depth (R32_FLOAT const) + motionVectors (R16G16_FLOAT const, pixel space)
    bool CreatePrepareInputs(float depthValue, float mvX, float mvY)
    {
        ReleasePrepareInputs();
        if (!CreateUploadPair(DXGI_FORMAT_R32_FLOAT, 4, depth, depthUpload, "depth")) return false;
        if (!CreateUploadPair(DXGI_FORMAT_R16G16_FLOAT, 4, mv, mvUpload, "motionVectors")) return false;

        std::vector<float> d(static_cast<size_t>(w) * static_cast<size_t>(h), depthValue);
        if (!UploadConstant(depth, depthUpload, d.data(), 4, "depth")) return false;

        const uint16_t hx = FloatToHalf(mvX), hy = FloatToHalf(mvY);
        std::vector<uint32_t> m(static_cast<size_t>(w) * static_cast<size_t>(h),
                                static_cast<uint32_t>(hx) | (static_cast<uint32_t>(hy) << 16));
        if (!UploadConstant(mv, mvUpload, m.data(), 4, "motionVectors")) return false;

        prepReady = true;
        return true;
    }

    void ReleasePrepareInputs()
    {
        depthUpload.Reset();
        mvUpload.Reset();
        depth.Reset();
        mv.Reset();
        prepReady = false;
    }

    bool CreateFrameResources()
    {
        const size_t frameBytes = static_cast<size_t>(w) * static_cast<size_t>(h) * 4;

        D3D12_RESOURCE_DESC td = {};
        td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = static_cast<UINT>(w);
        td.Height = static_cast<UINT>(h);
        td.DepthOrArraySize = 1;
        td.MipLevels = 1;
        td.Format = fmt;
        td.SampleDesc.Count = 1;
        td.Flags = D3D12_RESOURCE_FLAG_NONE;

        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_DEFAULT;
        HRESULT hr = device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &td,
                                                     D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                     IID_PPV_ARGS(src.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL src CreateCommittedResource hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

        D3D12_RESOURCE_DESC ub = {};
        ub.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        ub.Width = frameBytes;
        ub.Height = 1;
        ub.DepthOrArraySize = 1;
        ub.MipLevels = 1;
        ub.Format = DXGI_FORMAT_UNKNOWN;
        ub.SampleDesc.Count = 1;
        ub.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        D3D12_HEAP_PROPERTIES up = {};
        up.Type = D3D12_HEAP_TYPE_UPLOAD;
        hr = device->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &ub,
                                             D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                             IID_PPV_ARGS(srcUpload.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL srcUpload CreateCommittedResource hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }
        hr = srcUpload->Map(0, nullptr, &srcMapped);
        if (FAILED(hr)) { printf("FAIL srcUpload Map hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

        D3D12_RESOURCE_DESC gd = td;
        gd.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        hr = device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &gd,
                                             D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
                                             IID_PPV_ARGS(gen.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL gen CreateCommittedResource hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }
        hr = device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &gd,
                                             D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
                                             IID_PPV_ARGS(gen2.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL gen2 CreateCommittedResource hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

        // Staging readback BUFFER (upload/readback heaps accept buffers only).
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
        UINT64 stagingBytes = 0;
        device->GetCopyableFootprints(&td, 0, 1, 0, &fp, nullptr, nullptr, &stagingBytes);
        stagingPitch = fp.Footprint.RowPitch;

        D3D12_RESOURCE_DESC bd = {};
        bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = stagingBytes;
        bd.Height = 1;
        bd.DepthOrArraySize = 1;
        bd.MipLevels = 1;
        bd.Format = DXGI_FORMAT_UNKNOWN;
        bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        D3D12_HEAP_PROPERTIES rp = {};
        rp.Type = D3D12_HEAP_TYPE_READBACK;
        hr = device->CreateCommittedResource(&rp, D3D12_HEAP_FLAG_NONE, &bd,
                                             D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                             IID_PPV_ARGS(staging.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL staging CreateCommittedResource hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }
        hr = device->CreateCommittedResource(&rp, D3D12_HEAP_FLAG_NONE, &bd,
                                             D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                             IID_PPV_ARGS(staging2.GetAddressOf()));
        if (FAILED(hr)) { printf("FAIL staging2 CreateCommittedResource hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }
        return true;
    }

    void ReleaseFrameResources()
    {
        ReleasePrepareInputs();
        if (srcMapped != nullptr && srcUpload != nullptr)
        {
            srcUpload->Unmap(0, nullptr);
            srcMapped = nullptr;
        }
        srcUpload.Reset();
        src.Reset();
        gen.Reset();
        gen2.Reset();
        staging.Reset();
        staging2.Reset();
        srcUploadedOnce = false;
    }

    // Records upload-of-current-frame into src on the OPEN command list.
    void RecordSourceUpload(const std::vector<uint32_t>& pixels)
    {
        std::memcpy(srcMapped, pixels.data(), pixels.size() * 4);

        if (srcUploadedOnce)
        {
            Transition(cmdList.Get(), src.Get(), D3D12_RESOURCE_STATE_ALL_SHADER_RESOURCE,
                       D3D12_RESOURCE_STATE_COPY_DEST);
        }

        D3D12_TEXTURE_COPY_LOCATION dst = {};
        dst.pResource = src.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
        srcLoc.pResource = srcUpload.Get();
        srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        D3D12_RESOURCE_DESC td = src->GetDesc();
        device->GetCopyableFootprints(&td, 0, 1, 0, &srcLoc.PlacedFootprint, nullptr, nullptr, nullptr);
        cmdList->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, nullptr);

        Transition(cmdList.Get(), src.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                   D3D12_RESOURCE_STATE_ALL_SHADER_RESOURCE);
        srcUploadedOnce = true;
    }
};

// Output-state schemes probed for U3.
enum class OutScheme
{
    UavDeclared,      // outputs[0] declared UNORDERED_ACCESS (actual state UAV)
    PixelComputeRead, // outputs[0] declared PIXEL_COMPUTE_READ (actual ALL_SHADER_RESOURCE)
};

const char* SchemeName(OutScheme s)
{
    return s == OutScheme::UavDeclared ? "UNORDERED_ACCESS-declared"
                                       : "PIXEL_COMPUTE_READ-declared";
}

uint32_t SchemeFfxState(OutScheme s)
{
    return s == OutScheme::UavDeclared ? FFX_API_RESOURCE_STATE_UNORDERED_ACCESS
                                       : FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ;
}

D3D12_RESOURCE_STATES SchemeD3DState(OutScheme s)
{
    return s == OutScheme::UavDeclared
        ? D3D12_RESOURCE_STATE_UNORDERED_ACCESS
        : D3D12_RESOURCE_STATE_ALL_SHADER_RESOURCE;
}

// ---------------------------------------------------------------------------
// FG context lifecycle (U1)
// ---------------------------------------------------------------------------

struct FgCtx
{
    ffxContext fg = nullptr;
    bool configured = false;
    bool nullSwapchainAccepted = false; // U1 evidence
    bool dummySwapchainUsed = false;    // U1 fallback evidence

    HWND hwnd = nullptr;
    ComPtr<IDXGISwapChain4> dummy;
    ComPtr<IDXGIFactory2> factory;
};

ffxReturnCode_t CreateFgContext(const Rig& rig, int w, int h, uint32_t createFlags,
                                uint32_t backBufferFormat, uint32_t fgVersion, ffxContext* out)
{
    const auto& ffx = FfxRuntime::Functions();

    ffxCreateContextDescFrameGeneration fg = {};
    fg.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    fg.flags = createFlags;
    fg.displaySize.width = static_cast<uint32_t>(w);
    fg.displaySize.height = static_cast<uint32_t>(h);
    fg.maxRenderSize.width = static_cast<uint32_t>(w);
    fg.maxRenderSize.height = static_cast<uint32_t>(h);
    fg.backBufferFormat = backBufferFormat;

    ffxCreateBackendDX12Desc backend = {};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = rig.device.Get();

    ffxCreateContextDescFrameGenerationVersion ver = {};
    ver.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    ver.version = fgVersion; // selects the frame-generation provider (4.0.1 / 3.1.6)

    fg.header.pNext = &backend.header;
    backend.header.pNext = &ver.header;

    return ffx.CreateContext(out, &fg.header, nullptr);
}

// omitNoSwapchainFlag probes whether the NO_SWAPCHAIN_CONTEXT_NOTIFY flag
// itself is what suppresses generation (the flag is documented as "only run
// frame interpolation and not modify the swapchain").
ffxReturnCode_t ConfigureFgOffline(ffxContext fg, void* swapChain, int w, int h, uint64_t frameID,
                                   uint32_t extraFlags, bool omitNoSwapchainFlag)
{
    const auto& ffx = FfxRuntime::Functions();
    ffxConfigureDescFrameGeneration cfg = {};
    cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    cfg.swapChain = swapChain; // U1 probe: nullptr first
    cfg.presentCallback = nullptr;
    cfg.presentCallbackUserContext = nullptr;
    cfg.frameGenerationCallback = nullptr; // direct dispatch (sample m_UseCallback=false)
    cfg.frameGenerationCallbackUserContext = nullptr;
    cfg.frameGenerationEnabled = true;
    cfg.allowAsyncWorkloads = false;
    cfg.HUDLessColor = FfxApiResource{};
    cfg.flags = extraFlags;
    if (!omitNoSwapchainFlag)
        cfg.flags |= FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
    cfg.onlyPresentGenerated = false;
    cfg.generationRect.left = 0;
    cfg.generationRect.top = 0;
    cfg.generationRect.width = w;
    cfg.generationRect.height = h;
    cfg.frameID = frameID;
    return ffx.Configure(&fg, &cfg.header);
}

LRESULT CALLBACK DummyWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// U1 fallback: hidden-HWND dummy swapchain that is NEVER presented.
bool CreateDummySwapchain(FgCtx& c, ID3D12CommandQueue* queue, int w, int h)
{
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DummyWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"CatraFgOfflineDummy";
    if (RegisterClassExW(&wc) == 0)
    {
        printf("FAIL RegisterClassExW err=%lu\n", GetLastError());
        return false;
    }
    c.hwnd = CreateWindowExW(0, wc.lpszClassName, L"fg-offline-dummy", WS_POPUP,
                             0, 0, w, h, nullptr, nullptr, wc.hInstance, nullptr);
    if (c.hwnd == nullptr)
    {
        printf("FAIL CreateWindowExW err=%lu\n", GetLastError());
        return false;
    }

    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(c.factory.GetAddressOf()));
    if (FAILED(hr)) { printf("FAIL CreateDXGIFactory1 hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

    DXGI_SWAP_CHAIN_DESC1 d1 = {};
    d1.Width = static_cast<UINT>(w);
    d1.Height = static_cast<UINT>(h);
    d1.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d1.SampleDesc.Count = 1;
    d1.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d1.BufferCount = 2;
    d1.Scaling = DXGI_SCALING_NONE;
    d1.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d1.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    IDXGISwapChain1* sc1 = nullptr;
    hr = c.factory->CreateSwapChainForHwnd(queue, c.hwnd, &d1, nullptr, nullptr, &sc1);
    if (FAILED(hr) || sc1 == nullptr)
    {
        printf("FAIL CreateSwapChainForHwnd hr=0x%08lX\n", static_cast<unsigned long>(hr));
        return false;
    }
    hr = sc1->QueryInterface(IID_PPV_ARGS(c.dummy.GetAddressOf()));
    sc1->Release();
    if (FAILED(hr)) { printf("FAIL QI IDXGISwapChain4 hr=0x%08lX\n", static_cast<unsigned long>(hr)); return false; }

    IDXGIFactory* parent = nullptr;
    if (SUCCEEDED(c.dummy->GetParent(IID_PPV_ARGS(&parent))))
    {
        parent->MakeWindowAssociation(c.hwnd, DXGI_MWA_NO_WINDOW_CHANGES);
        parent->Release();
    }
    return true;
}

void DestroyDummySwapchain(FgCtx& c)
{
    c.dummy.Reset();
    c.factory.Reset();
    if (c.hwnd != nullptr)
    {
        DestroyWindow(c.hwnd);
        c.hwnd = nullptr;
    }
}

// Teardown in sample order: Configure(disable) then DestroyContext.
void TearDownFg(FgCtx& c, int w, int h, uint64_t frameID)
{
    const bool live = FfxRuntime::IsLoaded();
    const auto& ffx = FfxRuntime::Functions();
    if (c.fg != nullptr && live && c.configured)
    {
        ffxConfigureDescFrameGeneration cfg = {};
        cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        cfg.swapChain = c.dummy.Get(); // same pointer as at Configure time (nullptr ok)
        cfg.frameGenerationEnabled = false;
        cfg.flags = FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
        cfg.generationRect.width = w;
        cfg.generationRect.height = h;
        cfg.frameID = frameID;
        const ffxReturnCode_t rc = ffx.Configure(&c.fg, &cfg.header);
        if (rc != FFX_API_RETURN_OK)
        {
            printf("WARN Configure(disable) ret=%u (teardown continues)\n",
                   static_cast<unsigned>(rc));
        }
    }
    if (c.fg != nullptr && live)
    {
        ffx.DestroyContext(&c.fg, nullptr);
        c.fg = nullptr;
    }
    DestroyDummySwapchain(c);
}

// ---------------------------------------------------------------------------
// One recorded frame: source upload [+ Prepare] + FG dispatch + readback
// ---------------------------------------------------------------------------

struct DispatchResult
{
    ffxReturnCode_t prepRet = FFX_API_RETURN_OK;
    ffxReturnCode_t ret = FFX_API_RETURN_ERROR;
    Analysis an;
    Analysis an2;          // outputs[1] when numGeneratedFrames == 2
    bool gpuOk = false;
    bool executed = false;
    bool readback = false;
    bool readback2 = false;
};

DispatchResult DoDispatch(Rig& rig, ffxContext fg, float off, OutScheme scheme,
                          D3D12_RESOURCE_STATES& genState, uint64_t frameID, bool reset,
                          bool doPrepare, const FrameSpec& spec, uint32_t numGen,
                          const SampleGrid& grid, const char* dumpPrefix, bool analyze = true)
{
    DispatchResult dr;
    const auto& ffx = FfxRuntime::Functions();

    std::vector<uint32_t> cur;
    FrameSpec cs = spec;
    cs.off = off;
    FillFrame(cur, cs);

    if (!rig.BeginList()) return dr;

    rig.RecordSourceUpload(cur);

    // --- optional Prepare (depth + motion vectors + timing), sample order ---
    if (doPrepare)
    {
        if (!rig.prepReady)
        {
            printf("FAIL Prepare requested but depth/MV resources are not ready\n");
            rig.cmdList->Close();
            return dr;
        }
        ffxDispatchDescFrameGenerationPrepareV2 prep = {};
        prep.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
        prep.frameID = frameID;
        prep.flags = FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
        prep.commandList = rig.cmdList.Get();
        prep.renderSize.width = static_cast<uint32_t>(rig.w);
        prep.renderSize.height = static_cast<uint32_t>(rig.h);
        prep.jitterOffset.x = 0.0f;
        prep.jitterOffset.y = 0.0f;
        prep.motionVectorScale.x = 1.0f; // motion vectors in PIXEL space
        prep.motionVectorScale.y = 1.0f;
        prep.frameTimeDelta = 16.667f;   // 60 Hz cadence
        prep.reset = reset;
        prep.cameraNear = 0.1f;
        prep.cameraFar = 100.0f;
        prep.cameraFovAngleVertical = 1.0f;
        prep.viewSpaceToMetersFactor = 0.0f;
        prep.depth = ffxApiGetResourceDX12(rig.depth.Get(), FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
        prep.motionVectors = ffxApiGetResourceDX12(rig.mv.Get(), FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
        prep.cameraPosition[0] = 0.0f; prep.cameraPosition[1] = 0.0f; prep.cameraPosition[2] = 0.0f;
        prep.cameraUp[1] = 1.0f;
        prep.cameraRight[0] = 1.0f;
        prep.cameraForward[2] = -1.0f;

        dr.prepRet = ffx.Dispatch(&fg, &prep.header);
        if (dr.prepRet != FFX_API_RETURN_OK)
        {
            printf("PREPARE dispatch frameID=%llu ret=%u\n",
                   static_cast<unsigned long long>(frameID), static_cast<unsigned>(dr.prepRet));
            rig.cmdList->Close();
            return dr;
        }
    }

    ffxDispatchDescFrameGeneration dispatchFg = {};
    dispatchFg.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
    dispatchFg.commandList = rig.cmdList.Get();
    dispatchFg.presentColor = ffxApiGetResourceDX12(rig.src.Get(), FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    dispatchFg.outputs[0] = ffxApiGetResourceDX12(rig.gen.Get(), SchemeFfxState(scheme));
    dispatchFg.outputs[1] = (numGen >= 2)
        ? ffxApiGetResourceDX12(rig.gen2.Get(), SchemeFfxState(scheme))
        : FfxApiResource{};
    dispatchFg.outputs[2] = FfxApiResource{};
    dispatchFg.outputs[3] = FfxApiResource{};
    dispatchFg.numGeneratedFrames = numGen;
    dispatchFg.reset = reset;
    dispatchFg.backbufferTransferFunction = FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SRGB;
    dispatchFg.minMaxLuminance[0] = 0.0f;
    dispatchFg.minMaxLuminance[1] = 80.0f;
    dispatchFg.generationRect.left = 0;
    dispatchFg.generationRect.top = 0;
    dispatchFg.generationRect.width = rig.w;
    dispatchFg.generationRect.height = rig.h;
    dispatchFg.frameID = frameID;

    dr.ret = ffx.Dispatch(&fg, &dispatchFg.header);
    if (dr.ret != FFX_API_RETURN_OK)
    {
        printf("dispatch frameID=%llu ret=%u\n",
               static_cast<unsigned long long>(frameID), static_cast<unsigned>(dr.ret));
        rig.cmdList->Close();
        return dr;
    }

    // outputs[] come back in the declared state; transition -> COPY_SOURCE,
    // copy to staging, restore.
    const D3D12_RESOURCE_STATES declared = SchemeD3DState(scheme);
    Rig::Transition(rig.cmdList.Get(), rig.gen.Get(), genState,
                    D3D12_RESOURCE_STATE_COPY_SOURCE);

    D3D12_TEXTURE_COPY_LOCATION dst = {};
    dst.pResource = rig.staging.Get();
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint.Offset = 0;
    dst.PlacedFootprint.Footprint.Format = rig.fmt;
    dst.PlacedFootprint.Footprint.Width = static_cast<UINT>(rig.w);
    dst.PlacedFootprint.Footprint.Height = static_cast<UINT>(rig.h);
    dst.PlacedFootprint.Footprint.Depth = 1;
    dst.PlacedFootprint.Footprint.RowPitch = rig.stagingPitch;
    D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
    srcLoc.pResource = rig.gen.Get();
    srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    srcLoc.SubresourceIndex = 0;
    rig.cmdList->CopyTextureRegion(&dst, 0, 0, 0, &srcLoc, nullptr);

    Rig::Transition(rig.cmdList.Get(), rig.gen.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, declared);

    if (numGen >= 2)
    {
        Rig::Transition(rig.cmdList.Get(), rig.gen2.Get(), genState,
                        D3D12_RESOURCE_STATE_COPY_SOURCE);
        D3D12_TEXTURE_COPY_LOCATION dst2 = dst;
        dst2.pResource = rig.staging2.Get();
        D3D12_TEXTURE_COPY_LOCATION srcLoc2 = srcLoc;
        srcLoc2.pResource = rig.gen2.Get();
        rig.cmdList->CopyTextureRegion(&dst2, 0, 0, 0, &srcLoc2, nullptr);
        Rig::Transition(rig.cmdList.Get(), rig.gen2.Get(),
                        D3D12_RESOURCE_STATE_COPY_SOURCE, declared);
    }
    genState = declared;

    HRESULT hr = rig.cmdList->Close();
    if (FAILED(hr))
    {
        printf("FAIL list close hr=0x%08lX\n", static_cast<unsigned long>(hr));
        return dr;
    }
    if (!rig.SubmitAndWait()) return dr;
    dr.executed = true;
    dr.gpuOk = rig.DeviceHealthy();
    if (!dr.gpuOk)
    {
        printf("FAIL device removed reason=0x%08lX\n",
               static_cast<unsigned long>(rig.device->GetDeviceRemovedReason()));
        return dr;
    }

    void* mapped = nullptr;
    hr = rig.staging->Map(0, nullptr, &mapped);
    if (FAILED(hr))
    {
        printf("FAIL staging Map hr=0x%08lX\n", static_cast<unsigned long>(hr));
        return dr;
    }
    dr.readback = true;
    if (!analyze)
    {
        rig.staging->Unmap(0, nullptr);
        return dr; // U6 timing path: GPU work + readback only, no CPU analysis
    }
    if (dumpPrefix != nullptr)
    {
        char srcPath[512], outPath[512];
        std::snprintf(srcPath, sizeof(srcPath), "%s_source_off%.0f.bmp", dumpPrefix, off);
        std::snprintf(outPath, sizeof(outPath), "%s_generated_off%.0f.bmp", dumpPrefix, off);
        WriteBmp(outPath, static_cast<const uint8_t*>(mapped), rig.stagingPitch, rig.w, rig.h, rig.fmt);
        // source dump: tightly packed copy of the CPU mirror (same byte order)
        std::vector<uint8_t> packed(static_cast<size_t>(rig.w) * static_cast<size_t>(rig.h) * 4);
        std::memcpy(packed.data(), cur.data(), packed.size());
        WriteBmp(srcPath, packed.data(), static_cast<UINT>(rig.w * 4), rig.w, rig.h, rig.fmt);
        printf("dumped %s + %s\n", srcPath, outPath);
    }
    dr.an = Analyze(static_cast<const uint8_t*>(mapped), rig.stagingPitch, cs, grid, off);
    rig.staging->Unmap(0, nullptr);

    if (numGen >= 2)
    {
        void* mapped2 = nullptr;
        if (SUCCEEDED(rig.staging2->Map(0, nullptr, &mapped2)))
        {
            dr.readback2 = true;
            dr.an2 = Analyze(static_cast<const uint8_t*>(mapped2), rig.stagingPitch, cs, grid, off);
            if (dumpPrefix != nullptr)
            {
                char out2[512];
                std::snprintf(out2, sizeof(out2), "%s_generated2_off%.0f.bmp", dumpPrefix, off);
                WriteBmp(out2, static_cast<const uint8_t*>(mapped2), rig.stagingPitch, rig.w,
                         rig.h, rig.fmt);
                printf("dumped %s\n", out2);
            }
            rig.staging2->Unmap(0, nullptr);
        }
    }
    return dr;
}

// ---------------------------------------------------------------------------
// Phase runner (U2)
// ---------------------------------------------------------------------------

struct PhaseSpec
{
    const char* name;
    bool prepare;
    float mvSign;    // -1 / +1 (only used when prepare)
    bool rgba;       // backbuffer + textures as R8G8B8A8 instead of B8G8R8A8
    uint32_t extraConfigureFlags;
    bool dumpFrames;
    uint32_t fgVersion;          // FFX_FRAMEGENERATION_MAKE_VERSION(...)
    bool omitNoSwapchainFlag;    // Configure without NO_SWAPCHAIN_CONTEXT_NOTIFY
    uint32_t numGeneratedFrames; // 1 (or 2 to probe outputs[1])
};

struct PhaseResult
{
    std::string name;
    bool ran = false;
    int dispatches = 0, executedOk = 0, measured = 0;
    int interpHits = 0, passthroughHits = 0, prevCopyHits = 0;
    int interp2Hits = 0, pass2Hits = 0; // outputs[1] (numGeneratedFrames=2 probe)
    double meanBestT = 0.0, meanBlobT = 0.0, meanNcc = 0.0, meanT2 = 0.0;
    double tMin = 0.0, tMax = 0.0;
    int swapVotes = 0, mirrorVotes = 0, blobChanVotes[3] = {0, 0, 0};
    bool deviceOk = true;
    bool interp = false; // verdict: genuine interpolation in outputs[0] or [1]
};

PhaseResult RunPhase(Rig& rig, const PhaseSpec& ph, OutScheme scheme, int frames,
                     float blobX0, bool useDummySwapchain)
{
    PhaseResult pr;
    pr.name = ph.name;

    rig.ReleaseFrameResources();
    rig.fmt = ph.rgba ? DXGI_FORMAT_R8G8B8A8_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
    if (!rig.CreateFrameResources())
    {
        printf("[%s] FAIL frame-resource creation\n", ph.name);
        return pr;
    }
    const FrameSpec spec{rig.w, rig.h, rig.fmt, 0.0f, blobX0};
    const SampleGrid grid = MakeGrid(rig.w, rig.h);

    if (ph.prepare)
    {
        // Scene translates LEFT by g_shift px per frame: a pixel at x in the
        // current frame sat at x + g_shift in the previous frame. Both MV sign
        // conventions are probed (FSR documents pixel-space MV but the sign
        // convention is engine-side, so measure instead of assume).
        if (!rig.CreatePrepareInputs(0.5f, ph.mvSign * static_cast<float>(g_shift), 0.0f))
        {
            printf("[%s] FAIL Prepare input creation (depth/MV)\n", ph.name);
            return pr;
        }
    }

    FgCtx c;
    const uint32_t backFmt = ph.rgba ? FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM
                                     : FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM;
    ffxReturnCode_t rc = CreateFgContext(rig, rig.w, rig.h, 0, backFmt, ph.fgVersion, &c.fg);
    printf("[%s] CreateContext(fgVersion=0x%08X) ret=%u\n", ph.name,
           static_cast<unsigned>(ph.fgVersion), static_cast<unsigned>(rc));
    if (rc != FFX_API_RETURN_OK) return pr;

    void* sc = nullptr;
    if (useDummySwapchain)
    {
        if (!CreateDummySwapchain(c, rig.queue.Get(), rig.w, rig.h))
        {
            printf("[%s] FAIL dummy swapchain\n", ph.name);
            TearDownFg(c, rig.w, rig.h, 0);
            return pr;
        }
        sc = c.dummy.Get();
    }
    rc = ConfigureFgOffline(c.fg, sc, rig.w, rig.h, 0, ph.extraConfigureFlags,
                            ph.omitNoSwapchainFlag);
    printf("[%s] Configure(swapChain=%s, flags=0x%X%s) ret=%u (%s)\n", ph.name,
           sc == nullptr ? "nullptr" : "dummy", static_cast<unsigned>(ph.extraConfigureFlags),
           ph.omitNoSwapchainFlag ? " (NO_SWAPCHAIN_CONTEXT_NOTIFY omitted)" : "|NO_SWAPCHAIN_CTX",
           static_cast<unsigned>(rc), rc == FFX_API_RETURN_OK ? "OK" : "REJECTED");
    if (rc != FFX_API_RETURN_OK)
    {
        TearDownFg(c, rig.w, rig.h, 0);
        return pr;
    }
    c.configured = true;
    pr.ran = true;

    D3D12_RESOURCE_STATES genState = SchemeD3DState(scheme);
    // gen was created in UNORDERED_ACCESS; move it to the scheme state.
    if (genState != D3D12_RESOURCE_STATE_UNORDERED_ACCESS)
    {
        if (!rig.BeginList()) { TearDownFg(c, rig.w, rig.h, 0); return pr; }
        Rig::Transition(rig.cmdList.Get(), rig.gen.Get(),
                        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, genState);
        Rig::Transition(rig.cmdList.Get(), rig.gen2.Get(),
                        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, genState);
        if (FAILED(rig.cmdList->Close()) || !rig.SubmitAndWait())
        {
            TearDownFg(c, rig.w, rig.h, 0);
            return pr;
        }
    }

    uint64_t frameID = 0;
    float off = 0.0f;
    const int warmup = 2;
    double sumT = 0, sumBlobT = 0, sumNcc = 0, sumT2 = 0;
    pr.tMin = 1e9; pr.tMax = -1e9;
    for (int k = 0; k < frames && rig.DeviceHealthy(); ++k)
    {
        const bool warm = k < warmup;
        const char* dump = nullptr;
        char prefix[256];
        if (ph.dumpFrames && k == warmup + 3)
        {
            std::snprintf(prefix, sizeof(prefix), "fgspike_%s", ph.name);
            dump = prefix;
        }
        DispatchResult dr = DoDispatch(rig, c.fg, off, scheme, genState, frameID, frameID == 0,
                                       ph.prepare, spec, ph.numGeneratedFrames, grid, dump);
        off += static_cast<float>(g_shift);
        ++frameID;
        ++pr.dispatches;
        if (dr.ret == FFX_API_RETURN_OK && dr.executed) ++pr.executedOk;
        if (dr.ret != FFX_API_RETURN_OK || !dr.gpuOk || !dr.readback)
        {
            printf("[%s] dispatch #%d FAILED ret=%u prepRet=%u gpuOk=%d\n", ph.name, k,
                   static_cast<unsigned>(dr.ret), static_cast<unsigned>(dr.prepRet),
                   dr.gpuOk ? 1 : 0);
            pr.deviceOk = dr.gpuOk;
            break;
        }
        if (!warm)
        {
            ++pr.measured;
            sumT += dr.an.bestT;
            sumBlobT += dr.an.blobT;
            sumNcc += dr.an.bestNcc;
            pr.tMin = std::min(pr.tMin, dr.an.bestT);
            pr.tMax = std::max(pr.tMax, dr.an.bestT);
            pr.swapVotes += dr.an.bestSwap;
            pr.mirrorVotes += dr.an.bestMirror;
            if (dr.an.blobChan >= 0 && dr.an.blobChan < 3) pr.blobChanVotes[dr.an.blobChan]++;
            if (std::fabs(dr.an.bestT - 0.5) <= 0.20 && dr.an.bestNcc >= 0.90) ++pr.interpHits;
            if (std::fabs(dr.an.bestT) <= 0.10 && dr.an.bestNcc >= 0.90) ++pr.passthroughHits;
            if (std::fabs(dr.an.bestT - 1.0) <= 0.10 && dr.an.bestNcc >= 0.90) ++pr.prevCopyHits;
            if (dr.readback2)
            {
                sumT2 += dr.an2.bestT;
                if (std::fabs(dr.an2.bestT - 0.5) <= 0.20 && dr.an2.bestNcc >= 0.90) ++pr.interp2Hits;
                if (std::fabs(dr.an2.bestT) <= 0.10 && dr.an2.bestNcc >= 0.90) ++pr.pass2Hits;
            }
            printf("[%s] #%2d frameID=%llu off=%.0f | NCCbest t=%+.2f ncc=%.3f mad=%.1f %s%s | "
                   "t(0)=%.3f t(0.5)=%.3f t(1)=%.3f | blob chan=%d x=%.1f t=%+.2f peaks=%d nonBlack=%.2f",
                   ph.name, k, static_cast<unsigned long long>(frameID - 1), off - g_shift,
                   dr.an.bestT, dr.an.bestNcc, dr.an.bestMad,
                   dr.an.bestSwap ? "SWAP" : "idn ", dr.an.bestMirror ? " MIRROR" : "",
                   dr.an.nccPassthrough, dr.an.nccInterp, dr.an.nccPrevCopy,
                   dr.an.blobChan, dr.an.blobX, dr.an.blobT, dr.an.blobPeaks,
                   dr.an.nonBlackFrac);
            if (dr.readback2)
            {
                printf(" || out[1]: t=%+.2f ncc=%.3f mad=%.1f nonBlack=%.2f", dr.an2.bestT,
                       dr.an2.bestNcc, dr.an2.bestMad, dr.an2.nonBlackFrac);
            }
            printf("\n");
        }
        else
        {
            printf("[%s] #%2d [warmup] ret=%u nccbest t=%+.2f ncc=%.3f nonBlack=%.2f\n", ph.name, k,
                   static_cast<unsigned>(dr.ret), dr.an.bestT, dr.an.bestNcc, dr.an.nonBlackFrac);
        }
    }
    if (pr.measured == 0)
    {
        pr.tMin = 0.0;
        pr.tMax = 0.0;
    }
    if (pr.measured > 0)
    {
        pr.meanBestT = sumT / pr.measured;
        pr.meanBlobT = sumBlobT / pr.measured;
        pr.meanNcc = sumNcc / pr.measured;
        pr.meanT2 = sumT2 / pr.measured;
        pr.interp = (pr.executedOk == pr.dispatches) &&
                    (((pr.interpHits * 100 >= 70 * pr.measured) && pr.meanNcc >= 0.90) ||
                     (pr.interp2Hits * 100 >= 70 * pr.measured));
    }
    printf("[%s] SUMMARY dispatches=%d executedOk=%d measured=%d | out[0]: interpHits=%d "
           "passthroughHits=%d prevCopyHits=%d meanT(ncc)=%+.3f meanT(blob)=%+.3f range=[%+.2f,%+.2f] "
           "meanNcc=%.3f swapVotes=%d mirrorVotes=%d blobChan=R%d G%d B%d | out[1]: interpHits=%d "
           "passthroughHits=%d meanT=%+.3f\n",
           ph.name, pr.dispatches, pr.executedOk, pr.measured, pr.interpHits, pr.passthroughHits,
           pr.prevCopyHits, pr.meanBestT, pr.meanBlobT, pr.tMin, pr.tMax, pr.meanNcc,
           pr.swapVotes, pr.mirrorVotes,
           pr.blobChanVotes[0], pr.blobChanVotes[1], pr.blobChanVotes[2],
           pr.interp2Hits, pr.pass2Hits, pr.meanT2);
    printf("[%s] VERDICT: %s\n", ph.name, pr.interp ? "GENUINE INTERPOLATION (t~0.5)"
                                                    : "no interpolation detected");

    TearDownFg(c, rig.w, rig.h, frameID);
    rig.ReleasePrepareInputs();
    return pr;
}

} // namespace

// ---------------------------------------------------------------------------

int main(int argc, char** argv)
{
    printf("catra-fg-offline-test v2: FSR3/4 FG offline spike (subtask 03)\n");

    int width = 640, height = 360, frames = 12;
    bool runU6 = true;
    std::string onlyPhase;
    for (int i = 1; i < argc; ++i)
    {
        if (std::strcmp(argv[i], "--no-u6") == 0) { runU6 = false; continue; }
        if (i + 1 >= argc) break;
        if (std::strcmp(argv[i], "--width") == 0) width = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--height") == 0) height = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--frames") == 0) frames = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--shift") == 0) g_shift = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--phase") == 0) onlyPhase = argv[++i];
    }
    if (width < 128 || height < 128 || frames < 6 || g_shift < 2)
    {
        printf("FAIL bad args (min 128x128, 6 frames, shift>=2)\n");
        return 1;
    }
    const float blobX0 = static_cast<float>(width) - 96.0f;
    printf("validation: %dx%d, %d presented frames/phase, shift=%d px/frame, blobX0=%.0f\n",
           width, height, frames, g_shift, blobX0);
    printf("t semantics: 0=output==current present (passthrough), 0.5=genuine interpolation, "
           "1=output==previous present\n");

    try
    {
        // --- availability gate (CI-safe SKIP) -------------------------------
        if (!FfxRuntime::Load())
        {
            printf("SKIP FFX loader not loadable (DLLs absent) — graceful skip\n");
            return 0;
        }
        if (!FfxRuntime::ProbeDependencyDlls())
        {
            printf("SKIP FFX dependency DLLs missing — graceful skip\n");
            return 0;
        }
        FfxRuntime::InstallLogBridge();

        // --- adapter name (report evidence) ---------------------------------
        {
            ComPtr<IDXGIFactory1> fac;
            if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(fac.GetAddressOf()))))
            {
                ComPtr<IDXGIAdapter1> ad;
                if (SUCCEEDED(fac->EnumAdapters1(0, ad.GetAddressOf())))
                {
                    DXGI_ADAPTER_DESC1 d;
                    if (SUCCEEDED(ad->GetDesc1(&d)))
                    {
                        printf("adapter: %ls (vendor=0x%04X device=0x%04X)\n",
                               d.Description, d.VendorId, d.DeviceId);
                    }
                }
            }
        }

        Rig rig;
        if (!rig.CreatePlumbing()) return 0; // no DX12 GPU -> SKIP
        rig.w = width;
        rig.h = height;

        // FG provider version probe
        {
            ffxQueryDescGetVersions vd = {};
            vd.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
            vd.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
            vd.device = rig.device.Get();
            uint64_t count = 0;
            vd.outputCount = &count;
            ffxReturnCode_t rc = FfxRuntime::Functions().Query(nullptr, &vd.header);
            if (rc != FFX_API_RETURN_OK || count == 0)
            {
                printf("SKIP no FFX frame-generation provider (Query ret=%u count=%llu) — graceful skip\n",
                       static_cast<unsigned>(rc), static_cast<unsigned long long>(count));
                return 0;
            }
            std::vector<uint64_t> ids(static_cast<size_t>(count));
            std::vector<const char*> names(static_cast<size_t>(count));
            vd.versionIds = ids.data();
            vd.versionNames = names.data();
            rc = FfxRuntime::Functions().Query(nullptr, &vd.header);
            if (rc == FFX_API_RETURN_OK)
            {
                for (uint64_t i = 0; i < count; ++i)
                {
                    printf("FG provider version: id=0x%llx name=%s\n",
                           static_cast<unsigned long long>(ids[static_cast<size_t>(i)]),
                           names[static_cast<size_t>(i)] ? names[static_cast<size_t>(i)] : "?");
                }
            }
        }

        // ================= U1 — Configure with swapChain=nullptr ============
        bool nullAccepted = false;
        bool needDummy = false;
        bool u5GapRetOk = false;
        int u5Recover = 0;
        bool u5ViolationsOk = false;
        OutScheme chosenScheme = OutScheme::UavDeclared;
        {
            rig.fmt = DXGI_FORMAT_B8G8R8A8_UNORM;
            if (!rig.CreateFrameResources()) return 1;
            FgCtx c;
            ffxReturnCode_t rc = CreateFgContext(rig, width, height, 0,
                                                 FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM,
                                                 FFX_FRAMEGENERATION_VERSION, &c.fg);
            printf("U1 CreateContext(FG %dx%d, no swapchain) ret=%u (%s)\n", width, height,
                   static_cast<unsigned>(rc), rc == FFX_API_RETURN_OK ? "OK" : "FAIL");
            if (rc != FFX_API_RETURN_OK)
            {
                printf("SPIKE VERDICT: NO-GO (U1 CreateContext failed)\n");
                return 1;
            }
            rc = ConfigureFgOffline(c.fg, nullptr, width, height, 0, 0, false);
            printf("U1 Configure(swapChain=nullptr, enabled, NO_SWAPCHAIN_CONTEXT_NOTIFY) ret=%u (%s)\n",
                   static_cast<unsigned>(rc), rc == FFX_API_RETURN_OK ? "OK" : "REJECTED");
            if (rc == FFX_API_RETURN_OK)
            {
                nullAccepted = true;
                c.configured = true;
            }
            else
            {
                printf("U1 fallback: creating hidden dummy swapchain (never presented)\n");
                if (!CreateDummySwapchain(c, rig.queue.Get(), width, height))
                {
                    printf("SPIKE VERDICT: NO-GO (U1: nullptr rejected, dummy swapchain failed)\n");
                    TearDownFg(c, width, height, 0);
                    return 1;
                }
                rc = ConfigureFgOffline(c.fg, c.dummy.Get(), width, height, 0, 0, false);
                printf("U1 Configure(dummy swapchain) ret=%u (%s)\n",
                       static_cast<unsigned>(rc), rc == FFX_API_RETURN_OK ? "OK" : "FAIL");
                if (rc != FFX_API_RETURN_OK)
                {
                    printf("SPIKE VERDICT: NO-GO (U1: both nullptr and dummy swapchain rejected)\n");
                    TearDownFg(c, width, height, 0);
                    return 1;
                }
                needDummy = true;
                c.configured = true;
            }

            // ---- U3 — output resource-state scheme probe --------------------
            D3D12_RESOURCE_STATES genState = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
            OutScheme chosen = OutScheme::UavDeclared;
            bool schemeFound = false;
            FrameSpec spec{width, height, rig.fmt, 0.0f, blobX0};
            SampleGrid grid = MakeGrid(width, height);
            uint64_t frameID = 0;
            float off = 0.0f;
            for (OutScheme scheme : {OutScheme::UavDeclared, OutScheme::PixelComputeRead})
            {
                const D3D12_RESOURCE_STATES target = SchemeD3DState(scheme);
                if (genState != target)
                {
                    if (!rig.BeginList()) continue;
                    Rig::Transition(rig.cmdList.Get(), rig.gen.Get(), genState, target);
                    if (FAILED(rig.cmdList->Close()) || !rig.SubmitAndWait())
                    {
                        printf("U3 probe transition failed for scheme %s\n", SchemeName(scheme));
                        continue;
                    }
                    genState = target;
                }
                int nonBlack = 0, ok = 0;
                for (int i = 0; i < 3; ++i)
                {
                    DispatchResult dr = DoDispatch(rig, c.fg, off, scheme, genState, frameID,
                                                   frameID == 0, false, spec, 1, grid, nullptr);
                    off += static_cast<float>(g_shift);
                    ++frameID;
                    if (dr.ret == FFX_API_RETURN_OK && dr.executed) ++ok;
                    if (dr.gpuOk && dr.an.nonBlackFrac > 0.5) ++nonBlack;
                    printf("U3 probe [%s] #%d ret=%u nonBlack=%.2f ncc=%.3f t=%+.2f\n",
                           SchemeName(scheme), i, static_cast<unsigned>(dr.ret),
                           dr.an.nonBlackFrac, dr.an.bestNcc, dr.an.bestT);
                    if (!rig.DeviceHealthy()) break;
                }
                printf("U3 scheme %s: %d/3 dispatch executed, %d/3 non-black\n",
                       SchemeName(scheme), ok, nonBlack);
                if (ok == 3 && nonBlack == 3)
                {
                    chosen = scheme;
                    chosenScheme = scheme;
                    schemeFound = true;
                    break;
                }
            }
            if (!schemeFound)
            {
                printf("SPIKE VERDICT: NO-GO (U3: no resource-state scheme produced valid output)\n");
                TearDownFg(c, width, height, frameID);
                return 1;
            }
            printf("U3 chosen scheme: %s\n", SchemeName(chosen));

            // ---- U5 — frameID contract probes (on the U1/U3 context) -------
            const uint64_t gapFrame = frameID + 2; // deliberate contract violation
            DispatchResult gapDr = DoDispatch(rig, c.fg, off, chosen, genState, gapFrame, false,
                                              false, spec, 1, grid, nullptr);
            off += static_cast<float>(g_shift);
            printf("U5 gap dispatch (frameID %llu -> %llu, gap=+2): ret=%u nonBlack=%.2f ncc=%.3f\n",
                   static_cast<unsigned long long>(frameID),
                   static_cast<unsigned long long>(gapFrame),
                   static_cast<unsigned>(gapDr.ret), gapDr.an.nonBlackFrac, gapDr.an.bestNcc);
            frameID = gapFrame + 1;
            u5GapRetOk = (gapDr.ret == FFX_API_RETURN_OK) && gapDr.executed && gapDr.gpuOk;
            for (int i = 0; i < 4 && rig.DeviceHealthy(); ++i)
            {
                DispatchResult dr = DoDispatch(rig, c.fg, off, chosen, genState, frameID, false,
                                               false, spec, 1, grid, nullptr);
                off += static_cast<float>(g_shift);
                ++frameID;
                if (dr.ret == FFX_API_RETURN_OK && dr.executed && dr.gpuOk &&
                    dr.an.nonBlackFrac > 0.5)
                {
                    ++u5Recover;
                }
            }
            printf("U5 post-gap recovery: %d/4 valid dispatches (no error surfaced by the runtime)\n",
                   u5Recover);
            // backwards frameID (contract violation in the other direction)
            const uint64_t backFrame = (frameID >= 3) ? (frameID - 3) : 0;
            DispatchResult backDr = DoDispatch(rig, c.fg, off, chosen, genState, backFrame, false,
                                               false, spec, 1, grid, nullptr);
            off += static_cast<float>(g_shift);
            printf("U5 backwards dispatch (frameID %llu -> %llu): ret=%u executed=%d gpuOk=%d\n",
                   static_cast<unsigned long long>(frameID),
                   static_cast<unsigned long long>(backFrame),
                   static_cast<unsigned>(backDr.ret), backDr.executed ? 1 : 0,
                   backDr.gpuOk ? 1 : 0);
            frameID = backFrame + 1;
            // large forward jump
            const uint64_t jumpFrame = frameID + 100000;
            DispatchResult jumpDr = DoDispatch(rig, c.fg, off, chosen, genState, jumpFrame, false,
                                               false, spec, 1, grid, nullptr);
            off += static_cast<float>(g_shift);
            printf("U5 big-jump dispatch (frameID %llu -> %llu): ret=%u executed=%d gpuOk=%d\n",
                   static_cast<unsigned long long>(frameID),
                   static_cast<unsigned long long>(jumpFrame),
                   static_cast<unsigned>(jumpDr.ret), jumpDr.executed ? 1 : 0,
                   jumpDr.gpuOk ? 1 : 0);
            frameID = jumpFrame + 1;
            DispatchResult afterJumpDr = DoDispatch(rig, c.fg, off, chosen, genState, frameID, false,
                                                    false, spec, 1, grid, nullptr);
            ++frameID;
            printf("U5 after-jump dispatch (frameID %llu): ret=%u executed=%d gpuOk=%d nonBlack=%.2f\n",
                   static_cast<unsigned long long>(frameID - 1),
                   static_cast<unsigned>(afterJumpDr.ret), afterJumpDr.executed ? 1 : 0,
                   afterJumpDr.gpuOk ? 1 : 0, afterJumpDr.an.nonBlackFrac);
            if (backDr.ret == FFX_API_RETURN_OK && backDr.gpuOk && jumpDr.ret == FFX_API_RETURN_OK &&
                jumpDr.gpuOk && afterJumpDr.ret == FFX_API_RETURN_OK && afterJumpDr.gpuOk)
            {
                u5ViolationsOk = true; // contract violations are silently tolerated
            }
            TearDownFg(c, width, height, frameID);
            printf("U1 teardown (Configure disable + DestroyContext) OK\n");
        }
        printf("U5 frameID contract violations (gap/backwards/+100k jump): %s; post-gap recovery %d/4\n",
               u5ViolationsOk ? "all silently tolerated (ret=OK, no device loss)"
                              : "at least one violation surfaced an error",
               u5Recover);
        const bool u5Pass = u5GapRetOk && (u5Recover >= 3);

        // ================= U2 — phase matrix ================================
        const uint32_t kVer401 = FFX_FRAMEGENERATION_VERSION;               // 4.0.1
        const uint32_t kVer316 = FFX_FRAMEGENERATION_MAKE_VERSION(3, 1, 6); // 3.1.6 provider
        const PhaseSpec phases[] = {
            //  name                     prep   mvSign rgba  extraConfigureFlags                        dump   version  omitFlag numGen
            {"noprep-bgra",              false, 0.0f,  false, 0,                                       true,  kVer401, false, 1},
            {"prep-mvneg-bgra",          true,  -1.0f, false, 0,                                       true,  kVer401, false, 1},
            {"prep-mvpos-bgra",          true,  +1.0f, false, 0,                                       false, kVer401, false, 1},
            {"noprep-rgba",              false, 0.0f,  true,  0,                                       false, kVer401, false, 1},
            {"prep-mvneg-rgba",          true,  -1.0f, true,  0,                                       false, kVer401, false, 1},
            {"noprep-bgra-debugview",    false, 0.0f,  false,
             FFX_FRAMEGENERATION_FLAG_DRAW_DEBUG_VIEW,                                                  true,  kVer401, false, 1},
            {"noprep-bgra-fg316",        false, 0.0f,  false, 0,                                       true,  kVer316, false, 1},
            {"prep-mvneg-bgra-fg316",    true,  -1.0f, false, 0,                                       false, kVer316, false, 1},
            {"noprep-bgra-noflag",       false, 0.0f,  false, 0,                                       false, kVer401, true,  1},
            {"noprep-bgra-2gen",         false, 0.0f,  false, 0,                                       true,  kVer401, false, 2},
        };
        std::vector<PhaseResult> results;
        const OutScheme scheme = chosenScheme; // U3-chosen (see above)
        for (const PhaseSpec& ph : phases)
        {
            if (!onlyPhase.empty() && onlyPhase != ph.name) continue;
            printf("\n--- PHASE %s (prepare=%d mvSign=%+.0f rgba=%d cfgFlags=0x%X fgVer=0x%08X "
                   "omitNoSwapchainFlag=%d numGen=%u) ---\n",
                   ph.name, ph.prepare ? 1 : 0, ph.mvSign, ph.rgba ? 1 : 0,
                   static_cast<unsigned>(ph.extraConfigureFlags), static_cast<unsigned>(ph.fgVersion),
                   ph.omitNoSwapchainFlag ? 1 : 0,
                   static_cast<unsigned>(ph.numGeneratedFrames));
            results.push_back(RunPhase(rig, ph, scheme, frames, blobX0, needDummy));
        }

        // ================= verdict ==========================================
        printf("\n=== SPIKE RESULTS ===\n");
        printf("U1 Configure without a real swapchain: %s\n",
               nullAccepted ? "PASS (swapChain=nullptr accepted)"
                            : "PASS-via-workaround (dummy hidden swapchain, never presented)");
        printf("U3 output resource-state scheme: PASS (%s)\n", SchemeName(chosenScheme));
        printf("U5 frameID contract: gap/backwards/+100k jump tolerated=%s, post-gap recovery=%d/4 (%s)\n",
               u5ViolationsOk ? "yes" : "no", u5Recover,
               u5Pass ? "PASS" : "FAIL");
        int winner = -1;
        for (size_t i = 0; i < results.size(); ++i)
        {
            const PhaseResult& r = results[i];
            printf("U2 phase %-24s ran=%d interp=%s meanT(ncc)=%+.3f meanT(blob)=%+.3f ncc=%.3f "
                   "hits(interp/pass/prev)=%d/%d/%d out1(interp/pass)=%d/%d meanT2=%+.3f "
                   "swapVotes=%d mirrorVotes=%d\n",
                   r.name.c_str(), r.ran ? 1 : 0, r.interp ? "YES" : "no", r.meanBestT,
                   r.meanBlobT, r.meanNcc, r.interpHits, r.passthroughHits, r.prevCopyHits,
                   r.interp2Hits, r.pass2Hits, r.meanT2, r.swapVotes, r.mirrorVotes);
            if (r.interp && winner < 0) winner = static_cast<int>(i);
        }
        const bool u2Pass = winner >= 0;
        std::string winLabel = u2Pass ? (" (phase " + results[static_cast<size_t>(winner)].name + ")")
                                      : std::string();
        printf("U2 offscreen FG produces interpolated frames: %s%s\n",
               u2Pass ? "PASS" : "FAIL", winLabel.c_str());

        // ================= U6 — cost at 1920x1080 ===========================
        bool u6Ok = false;
        if (runU6)
        {
            const int w6 = 1920, h6 = 1080;
            const float blobX06 = static_cast<float>(w6) - 96.0f;
            // `winner` indexes `results` (which may be filtered by --phase), so
            // recover the matching PhaseSpec by name.
            PhaseSpec p6 = phases[0];
            if (winner >= 0)
            {
                const std::string& winName = results[static_cast<size_t>(winner)].name;
                for (const PhaseSpec& p : phases)
                {
                    if (winName == p.name) { p6 = p; break; }
                }
            }
            p6.name = "u6-1080p";
            p6.dumpFrames = false;
            printf("\n--- U6 timing @1920x1080 (config of phase %s) ---\n",
                   winner >= 0 ? results[static_cast<size_t>(winner)].name.c_str() : phases[0].name);
            rig.w = w6;
            rig.h = h6;
            rig.ReleaseFrameResources();
            rig.fmt = p6.rgba ? DXGI_FORMAT_R8G8B8A8_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
            if (rig.CreateFrameResources())
            {
                if (!p6.prepare || rig.CreatePrepareInputs(0.5f, p6.mvSign * g_shift, 0.0f))
                {
                    FgCtx c6;
                    const uint32_t backFmt = p6.rgba ? FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM
                                                     : FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM;
                    ffxReturnCode_t rc6 = CreateFgContext(rig, w6, h6, 0, backFmt, p6.fgVersion,
                                                          &c6.fg);
                    void* sc = nullptr;
                    if (rc6 == FFX_API_RETURN_OK && needDummy)
                    {
                        if (CreateDummySwapchain(c6, rig.queue.Get(), w6, h6)) sc = c6.dummy.Get();
                    }
                    if (rc6 == FFX_API_RETURN_OK)
                        rc6 = ConfigureFgOffline(c6.fg, sc, w6, h6, 0, p6.extraConfigureFlags,
                                                 p6.omitNoSwapchainFlag);
                    if (rc6 == FFX_API_RETURN_OK)
                    {
                        c6.configured = true;

                        FfxApiEffectMemoryUsage mem = {};
                        ffxQueryDescFrameGenerationGetGPUMemoryUsageV2 mq = {};
                        mq.header.type = FFX_API_QUERY_DESC_TYPE_FRAMEGENERATION_GPU_MEMORY_USAGE_V2;
                        mq.device = rig.device.Get();
                        mq.maxRenderSize.width = static_cast<uint32_t>(w6);
                        mq.maxRenderSize.height = static_cast<uint32_t>(h6);
                        mq.displaySize.width = static_cast<uint32_t>(w6);
                        mq.displaySize.height = static_cast<uint32_t>(h6);
                        mq.createFlags = 0;
                        mq.dispatchFlags = FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY |
                                           p6.extraConfigureFlags;
                        mq.backBufferFormat = backFmt;
                        mq.hudlessBackBufferFormat = FFX_API_SURFACE_FORMAT_UNKNOWN;
                        mq.gpuMemoryUsageFrameGeneration = &mem;
                        const ffxReturnCode_t mrc = FfxRuntime::Functions().Query(&c6.fg, &mq.header);
                        if (mrc == FFX_API_RETURN_OK)
                        {
                            printf("U6 GPU memory @1080p: total=%llu MB aliasable=%llu MB\n",
                                   static_cast<unsigned long long>(mem.totalUsageInBytes / (1024 * 1024)),
                                   static_cast<unsigned long long>(mem.aliasableUsageInBytes / (1024 * 1024)));
                        }
                        else
                        {
                            printf("U6 GPU memory query ret=%u (informational only)\n",
                                   static_cast<unsigned>(mrc));
                        }

                        D3D12_RESOURCE_STATES g6State = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
                        const OutScheme sc6 = scheme;
                        if (SchemeD3DState(sc6) != g6State)
                        {
                            if (rig.BeginList())
                            {
                                Rig::Transition(rig.cmdList.Get(), rig.gen.Get(), g6State,
                                                SchemeD3DState(sc6));
                                Rig::Transition(rig.cmdList.Get(), rig.gen2.Get(), g6State,
                                                SchemeD3DState(sc6));
                                if (SUCCEEDED(rig.cmdList->Close()) && rig.SubmitAndWait())
                                    g6State = SchemeD3DState(sc6);
                            }
                        }
                        const FrameSpec spec6{w6, h6, rig.fmt, 0.0f, blobX06};
                        const SampleGrid grid6 = MakeGrid(w6, h6);
                        uint64_t f6 = 0;
                        float off6 = 0.0f;
                        std::vector<double> times;
                        bool allOk = true;
                        for (int i = 0; i < 38 && rig.DeviceHealthy(); ++i)
                        {
                            const auto t0 = std::chrono::steady_clock::now();
                            DispatchResult dr = DoDispatch(rig, c6.fg, off6, sc6, g6State, f6,
                                                           f6 == 0, p6.prepare, spec6,
                                                           p6.numGeneratedFrames, grid6, nullptr,
                                                           i == 9 || i == 20 || i == 37);
                            const auto t1 = std::chrono::steady_clock::now();
                            const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
                            off6 += static_cast<float>(g_shift);
                            ++f6;
                            if (dr.ret != FFX_API_RETURN_OK || !dr.executed)
                            {
                                printf("U6 dispatch failed ret=%u\n", static_cast<unsigned>(dr.ret));
                                allOk = false;
                                break;
                            }
                            if (i >= 8) // 8 warm-up (shader compile / priming)
                            {
                                times.push_back(ms);
                                if (i == 9 || i == 20 || i == 37)
                                {
                                    printf("U6 dispatch #%d: %.2f ms (ncc=%.3f t=%+.2f nonBlack=%.2f)\n",
                                           i, ms, dr.an.bestNcc, dr.an.bestT, dr.an.nonBlackFrac);
                                }
                            }
                        }
                        if (allOk && !times.empty())
                        {
                            double sum = 0, mn = times[0], mx = times[0];
                            for (double t : times) { sum += t; mn = std::min(mn, t); mx = std::max(mx, t); }
                            std::vector<double> sorted = times;
                            std::sort(sorted.begin(), sorted.end());
                            const double avg = sum / static_cast<double>(times.size());
                            printf("U6 @1920x1080: n=%zu avg=%.2f ms median=%.2f ms min=%.2f ms "
                                   "max=%.2f ms (~%.1f generated frames/s end-to-end incl. source "
                                   "upload + readback + CPU analysis)\n",
                                   times.size(), avg, sorted[sorted.size() / 2], mn, mx, 1000.0 / avg);
                            u6Ok = true;
                        }
                        TearDownFg(c6, w6, h6, f6);
                    }
                    else
                    {
                        printf("U6 CreateContext/Configure @1080p failed ret=%u\n",
                               static_cast<unsigned>(rc6));
                        TearDownFg(c6, w6, h6, 0);
                    }
                }
            }
            rig.ReleaseFrameResources();
        }

        printf("\n=== FINAL ===\n");
        printf("U6 1080p timing: %s\n", u6Ok ? "measured (above)" : "NOT measured");
        const bool go = u2Pass && u5Pass;
        printf("SPIKE VERDICT: %s (U1 %s, U2 %s, U3 PASS, U5 %s, U6 %s)\n",
               go ? "GO" : "NO-GO", nullAccepted ? "nullptr-ok" : "dummy-swapchain",
               u2Pass ? "PASS" : "FAIL", u5Pass ? "PASS" : "FAIL",
               u6Ok ? "measured" : "not-measured");
        return go ? 0 : 1;
    }
    catch (const std::exception& e)
    {
        printf("FAIL unhandled exception: %s\n", e.what());
        return 1;
    }
    catch (...)
    {
        printf("FAIL unhandled non-std exception\n");
        return 1;
    }
}

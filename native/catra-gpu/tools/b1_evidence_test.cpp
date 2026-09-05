// b1_evidence_test.cpp — Standalone test to prove B1 fix (pre-loading FFX
// providers by absolute path) resolves the ret=4 NO_PROVIDER issue.
//
// This test calls FfxRuntime::Load() + FfxRuntime::IsAvailable() WITHOUT
// pre-loading any DLLs manually (unlike the original ffx_load_smoke_test
// which LoadLibrary's all 8 DLLs before FfxRuntime::Load). This exercises
// the exact code path the real app uses.
//
// Exit: 0 = FFX available (B1 fix working) | non-zero = unavailable.
#include "../ffx_runtime.h"
#include <cstdarg>
#include <cstdio>

namespace catra {
void BackendLog(int level, const char* fmt, ...)
{
    const char* lvl = "INFO";
    if (level == 2) lvl = "WARN";
    else if (level == 3) lvl = "ERROR";
    else if (level == 0) lvl = "DEBUG";

    va_list args;
    va_start(args, fmt);
    fprintf(stderr, "[%s] ", lvl);
    vfprintf(stderr, fmt, args);
    fprintf(stderr, "\n");
    va_end(args);
}
} // namespace catra

int main()
{
    printf("B1 evidence test: FFX probe WITHOUT manual DLL pre-load\n");
    printf("If B1 fix works, providers are pre-loaded by FfxRuntime::Load()\n");
    printf("and ffxQuery returns >= 1 upscale version (not ret=4 NO_PROVIDER).\n\n");

    // Call IsAvailable() which internally calls Load() -> pre-loads providers.
    const bool available = catra::ffx::FfxRuntime::IsAvailable();
    printf("\n=== RESULT: FfxRuntime::IsAvailable() = %s ===\n",
           available ? "TRUE (B1 fix working)" : "FALSE (B1 fix NOT working)");

    return available ? 0 : 1;
}

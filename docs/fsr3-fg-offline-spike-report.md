# Spike report — FSR3 FG offline (CA-S3.1)

Relatório canônico movido para **[`docs/fsr/fg_offline_spike.md`](fsr/fg_offline_spike.md)**
(convenção `docs/fsr/` do repositório).

**Veredito: NO-GO** — U1 PASS (`swapChain=nullptr` aceito pelo provider FG
4.0.1), **U2 FAIL** (dispatch offscreen = passthrough em 10/10 frames × 7
fases; provider 3.1.6 recusa CreateContext sem swapchain, ret=6), U3 PASS,
U5 PASS com ressalva, U6 medido (~22–24 ms/frame @1080p em passthrough).
Evidência de controle: playback (swapchain FG + Present) gera frames na mesma
máquina (`generated=90, ratio=2.00`). Logs:
`native/catra-gpu/build/Release/fg_offline_run_v2.log` / `v3.log`.

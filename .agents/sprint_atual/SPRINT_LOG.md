# Sprint Log — CATRA Fase 2 (Pre-Processamento GPU)

**Fase 1:** CONCLUÍDA — tag v0.1.0, 290 testes, arquivada em .agents/Learning/sprints/SPRINT_01/
**Fase 2 início:** 2026-08-02

## ⚠️ Restrição ambiental (Fase 2)
- SEM MSVC (cl.exe) / SEM Visual Studio 2022 / SEM vcpkg → **build nativo C++ NÃO verificável aqui**
- FSR 4 SDK (ST-14) é license-gated (GPUOpen) → não obtível automaticamente
- Padrão adotado (como ST-05/08/09): código nativo completo + scripts de build; build/run nativo = validação MANUAL (máquina com VS2022 C++ + vcpkg + SDKs AMD). Camada C# testada com fakes.

## Status
| ST | Título | Dev | Status | Review | QA | Commit | Retries |
|----|--------|-----|--------|--------|----|--------|---------|
| ST-12 | Native bridge C++ skeleton | developer-alto | 🔄 | — | — | — | 0 |
| ST-13 | RIFE v4 interp | developer-critical | ⏳ | — | — | — | 0 |
| ST-14 | FSR 4 upscale | developer-critical | ⏳ | — | — | — | 0 |
| ST-15 | D3D11→DX12 interop | developer-alto | ⏳ | — | — | — | 0 |
| ST-16 | AMF H.265 encode | developer-alto | ⏳ | — | — | — | 0 |
| ST-17 | Pipeline orchestration C# | developer-critical | ⏳ | — | — | — | 0 |
| ST-18 | Janela deslizante | developer-alto | ⏳ | — | — | — | 0 |
| ST-19 | UI pre-processar | developer-medio | ⏳ | — | — | — | 0 |
| ST-20 | Playback/DLNA processado | developer-medio | ⏳ | — | — | — | 0 |
| ST-21 | Cleanup | developer-baixo | ⏳ | — | — | — | 0 |
| ST-22 | Settings processamento | developer-baixo | ⏳ | — | — | — | 0 |

## Lotes (Fase 2) — sequenciais (arquivos nativos compartilhados: CMakeLists/vcpkg.json/catra_gpu.cpp)
1. ST-12 | 2. ST-13 → ST-14 | 3. ST-15 → ST-16 | 4. ST-17 | 5. ST-18 | 6. ST-19 → ST-20 | 7. ST-21 → ST-22

## Eventos
- (preencher durante execução)

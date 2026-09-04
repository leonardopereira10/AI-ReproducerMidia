# CATRA v0.6.0 — versão estável

Primeira versão pública. SPRINT_05 concluído (Web Control Panel + Streaming
Home). Build com 0 erros, **653 testes automatizados passando**
(Core 15 · UI 139 · Data 20 · Services 479).

> **Nota de transparência:** projeto inteiramente desenvolvido por IA
> (orquestração multi-agente com gates de build, revisão e QA). Os percentuais
> de confiança abaixo refletem: cobertura de testes automatizados, validação em
> hardware real (POC em máquina AMD RDNA 4) e bugs conhecidos. Nada aqui foi
> validado em produção.

## Funcionalidades e confiança

| Funcionalidade | Confiança | Base |
|---|---|---|
| Biblioteca: scanner + parser de nomes | **90%** | testada + uso contínuo |
| Reprodução local (D3D11VA → fallback software) | **85%** | validada em hardware AMD; fallbacks exercitados em testes |
| Continuar assistindo / watch state | **90%** | testada |
| Thumbnails | **85%** | testada |
| Tema light/dark | **90%** | trivial, testada |
| RIFE interpolação (DirectML → CPU) | **85%** | validada em AMD; cascatas de fallback testadas |
| Upscale FSR 1 (qualquer GPU) | **85%** | autocontido, validada em AMD; vendor-neutra por construção |
| Upscale FSR 4 (FFX runtime) | **60%** | 🐛 bug conhecido: máquina com ecossistema FSR4 executa sempre o fallback — correção na Sprint 06 |
| Encode AMF H.265 (GPU AMD) | **80%** | validada em AMD; **sem fallback fora de AMD ainda** (Sprint 06) |
| Frame generation FSR 3 no processamento | **10%** | 🐛 não funcional: opção cai silenciosamente para RIFE — ataque principal da Sprint 06 |
| Fila de processamento / janela deslizante / perfis | **85%** | testada |
| Painel web de controle remoto | **80%** | sprint recente com testes; races de estado já observados e corrigidos |
| Streaming HTML5 + perfis + browser mode | **80%** | testada; dependente de rede local |
| DLNA casting | **75%** | funcional; sensível a dispositivos/edge cases de rede |
| Uso em máquina **sem GPU AMD** | **20%** | não validada; alvo integral da Sprint 06 |

## Validação desta versão

- Máquina de referência: AMD RDNA 4 (Adrenalin) — decode, upscale, encode e playback
- Suíte: `dotnet test CATRA.sln` — 653/653
- Build gate obrigatório por subtask no workflow de desenvolvimento

## Limitações conhecidas

1. **FSR 4 sempre em fallback** mesmo em hardware compatível (diagnóstico na Sprint 06)
2. **FSR 3 frame generation** não aplica o algoritmo no processamento (fallback silencioso para RIFE)
3. **Export falha em máquinas sem AMD** (encoder AMF sem fallback — cascata planejada na Sprint 06)
4. Validação em NVIDIA/Intel pendente de hardware

## Em desenvolvimento (Sprint 06)

- Cascata de encoder: AMF → FFmpeg hw (NVENC/QSV) → libx265 software
- FSR 3 frame generation offline no processamento (spike de viabilidade GO provável)
- Notificação de fallbacks na tela de processamento (dedup por job)
- Correção da disponibilidade FSR 4 + UI de disponibilidade real

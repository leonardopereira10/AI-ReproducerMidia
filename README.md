# CATRA — AI Reprodutor de Mídia

Reprodutor de mídia desktop (Windows/WPF) com pipeline de **processamento GPU**
para melhoria de vídeo: interpolação de quadros (RIFE), upscale (FSR 1 / FSR 4),
frame generation (FSR 3, em desenvolvimento) e encoding H.265 (AMF) — com
controle remoto e streaming via **painel web** na rede local.

> ⚠️ **Projeto 100% desenvolvido por IA** (orquestração multi-agente).
> Funcionalidades são validadas por suíte automatizada (650+ testes) e POC em
> hardware AMD RDNA 4. Consulte os níveis de confiança no
> [release v0.6.0](../../releases) antes de usar em produção.

## Funcionalidades

### Reprodução e biblioteca
- Reprodução local com decode **FFmpeg D3D11VA** (fallback automático para
  software) e renderização DirectX 11 via `HwndHost`
- Áudio via WASAPI com sincronização A/V
- Scanner de biblioteca com parser de nomes (séries/filmes/episódios)
- "Continuar assistindo" com threshold de progresso configurável
- Thumbnails geradas por frame capture
- Tema claro/escuro seguindo o Windows

### Processamento GPU (pipeline nativo C++20)
- **RIFE v4** — interpolação de quadros (ONNX Runtime: DirectML → CPU)
- **FSR 1 (EASU)** — upscale autocontido, qualquer GPU DX11/DX12
- **FSR 4** — upscale via FidelityFX SDK carregado em runtime (fallback FSR4→FSR1)
- **AMF H.265** — encode hardware em GPUs AMD (opcional em build)
- Interop D3D11↔DX12 zero-copy (NT shared handles + keyed mutex)
- Fila de processamento com janela deslizante de episódios e perfis
- Reprodução/DLNA usam automaticamente o arquivo processado quando disponível

### Controle remoto e streaming
- Painel web mobile-first (celular/tablet na mesma rede)
- Descoberta **DLNA** e casting raw
- Streaming HTML5 com perfis de resolução/qualidade
- Browser mode com sincronização de playback via WebSocket
- API REST de biblioteca (categorias, busca, continue-assistindo)

## Arquitetura

```
src/
  CATRA.App        WPF, composição/DI, host do player
  CATRA.Core       Interfaces, modelos de domínio, contratos
  CATRA.Data       SQLite, repositórios, scanner/parser, fila
  CATRA.Services   Playback, processamento, streaming, web control, casting
  CATRA.UI         Views, ViewModels, controles e estilos
native/
  catra-gpu        Bridge C++20 com C ABI plana (P/Invoke), CMake + vcpkg
tests/             xUnit + FluentAssertions (4 projetos)
docs/              Troubleshooting e documentação consolidada
```

Pipeline GPU:

```
FFmpeg D3D11VA decode (NV12)
  → NV12→BGRA (compute shader D3D11)
  → RIFE interpolação (D3D11, ONNX Runtime)
  → FSR upscale (FSR1 D3D11 compute / FSR4 via FFX runtime)
  → AMF encode H.265 (D3D12, opcional)
```

O bridge nativo expõe **C ABI plana** (`extern "C"`, handles inteiros, códigos
de erro) com barreira de exceção em todo entry point. As DLLs do FidelityFX são
carregadas em runtime (`LoadLibrary`), sem dependência de build no SDK.

## Build

Pré-requisitos: **.NET 8 SDK**, **Visual Studio 2022** (workload C++/CMake) ou
CMake 3.25+, vcpkg.

```powershell
# .NET (inclui o build nativo via target BeforeBuild)
dotnet build CATRA.sln

# Nativo isolado
.\scripts\build-native.ps1
# ou
.\build_native_now.bat

# Testes
dotnet test CATRA.sln
```

Binários de terceiros sob `lib/` (FFmpeg, ONNX Runtime, modelos RIFE, headers
AMF) são baixados/fornecidos on demand — ver `scripts/` e comentários no
`.gitignore`. O SDK FidelityFX 2.3.0 (headers, MIT) acompanha o repo.

## Roadmap

Sprint 06 em andamento: **processamento FSR1/FSR3 independente de GPU AMD** —
cascata de encoder com fallback (AMF → NVENC/QSV → libx265), frame generation
FSR 3 no processamento offline, notificação de fallbacks na UI e correção do
bug de disponibilidade FSR 4. Planejamento em `.agents/specs/`.

## Licença

[MIT](LICENSE) — inclui componentes MIT de terceiros (FidelityFX SDK headers).
FFmpeg/ONNX Runtime são baixados separadamente e possuem licenças próprias.

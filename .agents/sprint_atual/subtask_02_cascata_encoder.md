# Subtask 02 — Cascata de encoder com fallback (AMF → FFmpeg hw → libx265)

| Campo       | Valor                                                |
|-------------|------------------------------------------------------|
| **Story**   | Story 02 — Cascata de encoder com fallback (AMF → FFmpeg hardware → x265 software) |
| **Tipo**    | dev                                                  |
| **Complexidade** | alta                                             |
| **Agente**  | developer-alta                                       |
| **Dependências** | nenhuma (pode rodar em paralelo com Story 01; coordenação com Story 04 via evento tipado) |

## Descrição

Hoje o pipeline cria o encoder AMF sem fallback
(`src/CATRA.Services/Processing/ProcessingPipeline.cs:267`):
`bridge.CreateEncoder(...)` → `ThrowIfError` → se `CATRA_ERR_DEVICE` (-4,
GPU não-AMD / sem `amfrt64.dll`) ou `CATRA_ERR_NOT_IMPL` (-2, build sem
`CATRA_AMF_ROOT`), o job inteiro falha. **Único hard blocker** para o app
funcionar end-to-end fora de máquina AMD.

Implementar a cascata: **AMF nativo → FFmpeg com encoder de hardware
(NVENC → QSV) → libx265 software**. Tanto `ERR_DEVICE` quanto
`ERR_NOT_IMPL` são gatilhos de fallback — nenhum dos dois derruba o job.

### Decisão de Design: Readback GPU→CPU para FFmpeg

**Problema**: o AMF recebe texturas `ID3D12Resource*` (BGRA, do upscale
FSR1/FSR4 ou da conversão NV12→BGRA do decoder). O FFmpeg CLI é um
processo externo — precisa de bytes CPU. Como obter os pixels raw de uma
textura D3D12 no managed side?

**Opções avaliadas**:

| Opção | Descrição | Veredicto |
|-------|-----------|-----------|
| A. Readback via bridge (nova função nativa) | Adicionar `catra_texture_readback_bgra()` no native que faz D3D12 readback buffer + CopyResource + fence + Map, retorna bytes BGRA via out-params | **ESCOLHIDA** — encapsula complexidade GPU, C# recebe `byte[]` limpo |
| B. Reusar `ReadBack11ToCpu` do interop | Copiar D3D12→D3D11 staging→Map | Rejeitada — interop pool tem keyed mutex, lifecycle acoplado, risco de regressão |
| C. Ler textura no C# via SharpDX/Vortice | Rejeitada — introduz nova dependência GPU no managed | Rejeitada |
| D. FFmpeg com input GPU (hwupload) | Rejeitada — FFmpeg CLI não aceita texture handle; pipe exige dados CPU | Rejeitada |

**`catra_texture_readback_bgra(void* texture, uint8_t** out_data, int* out_size)`**:
- Detecta formato via `GetDesc()` (BGRA ou NV12).
- **BGRA** (caminho principal — upscale FSR1 produz `DXGI_FORMAT_B8G8R8A8_UNORM`):
  readback buffer D3D12 (`D3D12_HEAP_TYPE_READBACK`), `CopyResource`, fence signal,
  `Map`, `memcpy` para output buffer. Row pitch = `width * 4`, size = `w * h * 4`.
- **NV12** (passthrough sem upscale — raro mas possível):
  readback buffer com `GetCopyableFootprints` para 2 subresources (Y + UV),
  Map, cópia planar para output contíguo (Y seguido de UV). Size = `w * h * 3 / 2`.
- Output é alocado pelo bridge com `catra_alloc()` (mesmo padrão dos outros
  entry points); caller libera com `catra_free()`.
- Reusa o mesmo D3D12 device/queue do bridge (`g_d3d12Device`, `g_directQueue`).
- Estado da textura de entrada: deve estar em `COPY_SOURCE` ou `COMMON`.
  O upscale FSR1 já entrega em estado compatível (a texture é shared via interop
  pool, sem UAV active no momento do encode). Se necessário, barrier interno.

### Decisão de Design: Formato de pixel uniforme

Todo o pipeline já opera em **BGRA**:
- Upscale FSR1: output `DXGI_FORMAT_B8G8R8A8_UNORM` (upscale_fsr1.cpp:345).
- Decoder (sem upscale): `TryGpuConvert` roda shader NV12→BGRA antes do frame
  entrar no canal.
- Interpolação RIFE: output BGRA (interp_rife.cpp:1598).
- AMF na máquina dev: inicializado com `AMF_SURFACE_BGRA`.

O FFmpeg CLI receberá `-pix_fmt bgra` via stdin. Uniforme, sem conversão
extra no readback.

### Decisão de Design: Onde vive o readback

O readback vive **dentro do FFmpegEncoder**, não no pipeline. Motivo:
- Pipeline não deve saber se o encoder é AMF ou FFmpeg (polimorfismo).
- `IVideoEncoder.EncodeFrame(IntPtr texture)` — AMF usa texture direto,
  FFmpeg faz readback internamente e pipeia para o processo.
- Bridge ganha método `ReadbackTextureToCpu()` que qualquer componente
  pode usar (futuro: thumbnail generation, etc).

## Arquivos Alvo (fileScope)

### Novos arquivos

| Arquivo | Responsabilidade |
|---------|-----------------|
| `src/CATRA.Core/Interfaces/IVideoEncoder.cs` | Interface de encoder: `EncodeFrame`, `Flush`, `SelectedEncoder`, `Dispose` |
| `src/CATRA.Core/Processing/EncoderFallbackEventArgs.cs` | Evento tipado: `(from, to, reason)` para Story 04 |
| `src/CATRA.Services/Processing/AmfBridgeEncoder.cs` | `IVideoEncoder` delegando ao AMF via bridge (wrapper do código existente) |
| `src/CATRA.Services/Processing/FFmpegCliEncoder.cs` | `IVideoEncoder` via processo FFmpeg CLI + readback |
| `src/CATRA.Services/Processing/EncoderFallbackFactory.cs` | Cascata AMF → FFmpeg hw → libx265; retorna `IVideoEncoder` |

### Arquivos modificados

| Arquivo | Mudança |
|---------|---------|
| `src/CATRA.Core/Interfaces/INativeBridge.cs` | Adicionar `ReadbackTextureToCpu(IntPtr, out byte[])` |
| `src/CATRA.Services/Processing/NativeBridge.cs` | Implementar readback: P/Invoke `catra_texture_readback_bgra` + `catra_free` |
| `native/catra-gpu/catra_gpu.h` | Declarar `catra_texture_readback_bgra()` |
| `native/catra-gpu/catra_gpu.cpp` | Implementar readback (D3D12 readback buffer + CopyResource + fence + Map) |
| `src/CATRA.Services/Processing/ProcessingPipeline.cs` | Linha 267: trocar `bridge.CreateEncoder()` por `EncoderFallbackFactory.Create()`; usar `IVideoEncoder` no frame loop e flush; emitir evento de fallback |
| `src/CATRA.Core/Processing/ProcessResult.cs` (ou inline) | Adicionar campo `SelectedEncoder` ao resultado (observabilidade, CA-2.5) |

### Testes

| Arquivo | Escopo |
|---------|--------|
| `tests/CATRA.Services.Tests/Processing/EncoderFallbackFactoryTests.cs` | Cascata: AMF OK → AMF; AMF ERR_DEVICE → FFmpeg; AMF ERR_NOT_IMPL → FFmpeg; ordem NVENC→QSV→libx265; eventos disparados |
| `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs` | Adicionar casos: fallback encoder no pipeline; `FakeNativeBridge` com `CreateEncoder` throwing -4/-2 |

## Passos

### Passo 1 — Abstração `IVideoEncoder`

1. Criar `src/CATRA.Core/Interfaces/IVideoEncoder.cs`:
   ```csharp
   public interface IVideoEncoder : IDisposable
   {
       /// <summary>Nome do encoder ativo (ex.: "AMF", "hevc_nvenc", "hevc_qsv", "libx265").</summary>
       string SelectedEncoder { get; }

       /// <summary>Codifica um frame. texture é ID3D12Resource* para AMF,
       /// ou qualquer textura GPU legível pelo bridge para FFmpeg (readback interno).</summary>
       void EncodeFrame(IntPtr texture, out IntPtr packetBuffer, out int packetSize);

       /// <summary>Drain final. Mesma semântica de FlushEncoder atual.</summary>
       void Flush(out IntPtr packetBuffer, out int packetSize);
   }
   ```
2. Design notes:
   - `IntPtr texture` funciona para ambos: AMF usa como `ID3D12Resource*` direto;
     FFmpeg usa bridge `ReadbackTextureToCpu()` internamente.
   - `packetBuffer` / `packetSize` — mesma semântica do AMF atual (ponteiro
     context-owned válido até próxima chamada). Para FFmpeg: buffer alocado
     pelo encoder, válido até próxima chamada. Pipeline não precisa saber
     a diferença.
   - `Dispose()` libera recursos (destroy encoder AMF, matar processo FFmpeg).

### Passo 2 — `AmfBridgeEncoder`

1. Criar `src/CATRA.Services/Processing/AmfBridgeEncoder.cs`:
   ```csharp
   internal sealed class AmfBridgeEncoder : IVideoEncoder
   {
       private readonly INativeBridge _bridge;
       private readonly IntPtr _context;
       public string SelectedEncoder => "AMF";

       public AmfBridgeEncoder(INativeBridge bridge, IntPtr context)
       { _bridge = bridge; _context = context; }

       public void EncodeFrame(IntPtr texture, out IntPtr buf, out int size)
           => _bridge.EncodeFrame(_context, texture, out buf, out size);

       public void Flush(out IntPtr buf, out int size)
           => _bridge.FlushEncoder(_context, out buf, out size);

       public void Dispose() => _bridge.DestroyEncoder(_context);
   }
   ```
2. Wrapper trivial — zero mudança de comportamento no caminho AMF.

### Passo 3 — Readback no bridge

1. `native/catra-gpu/catra_gpu.h` — adicionar:
   ```c
   // Readback: copia textura D3D12 (BGRA ou NV12) para buffer CPU contíguo.
   // *out_data é alocado via catra_alloc; caller libera com catra_free.
   // *out_format recebe DXGI_FORMAT (para o caller saber o pix_fmt).
   // *out_pitch recebe row pitch em bytes (para BGRA; para NV12 é o Y pitch).
   // Retorna CATRA_OK, CATRA_ERR_DEVICE (readback falhou), CATRA_ERR_INVALID_ARG.
   CATRA_API int catra_texture_readback_bgra(
       void* texture,
       uint8_t** out_data, int* out_size,
       uint32_t* out_format, uint32_t* out_pitch);
   ```
2. `native/catra-gpu/catra_gpu.cpp` — implementar:
   - QI `ID3D12Resource` do `texture` (pode ser D3D11 ou D3D12; se D3D11,
     usar shared handle para obter D3D12 equivalent, OU fazer staging via
     D3D11 immediate context + `ReadBack11ToCpu`).
   - **Caminho D3D12** (principal):
     a. `GetDesc()` → format, width, height.
     b. `GetCopyableFootprints()` → total size, row pitches.
     c. `CreateCommittedResource(READBACK, COPY_DEST)`.
     d. `CopyResource(readback, src)`.
     e. Fence signal + wait (padrão do bridge: `g_directQueue->Signal`,
        `fence->SetEventOnCompletion`, `WaitForSingleObject`).
     f. `Map(0)` → `memcpy` para `catra_alloc(total)`.
     g. `Unmap`.
   - **Caminho D3D11** (textura decoder sem upscale):
     a. QI `ID3D11Texture2D`.
     b. Reusar `ReadBack11ToCpu()` (já existe em d3d_interop.cpp,
        linha 1079-1110) — copy + staging + Map.
     c. Copiar para `catra_alloc`.
   - Setar `*out_format`, `*out_size`, `*out_pitch`.
3. `src/CATRA.Core/Interfaces/INativeBridge.cs` — adicionar:
   ```csharp
   /// <summary>Lê uma textura GPU (D3D11 ou D3D12) de volta para bytes CPU.
   /// Retorna formato DXGI e row pitch para interpretação correta.</summary>
   void ReadbackTextureToCpu(IntPtr texture, out byte[] pixels,
       out uint dxgiFormat, out uint rowPitch);
   ```
4. `src/CATRA.Services/Processing/NativeBridge.cs` — implementar:
   - P/Invoke `catra_texture_readback_bgra` → `byte*` + size.
   - `Marshal.Copy` para `byte[]` managed.
   - `catra_free` o buffer nativo.

### Passo 4 — `FFmpegCliEncoder`

1. Criar `src/CATRA.Services/Processing/FFmpegCliEncoder.cs`:
   ```csharp
   internal sealed class FFmpegCliEncoder : IVideoEncoder, IDisposable
   {
       private readonly INativeBridge _bridge;
       private readonly Process _ffmpeg;
       private readonly int _width, _height;
       private byte[] _outputBuffer; // reused per frame
       private bool _disposed;

       public string SelectedEncoder { get; } // "hevc_nvenc" | "hevc_qsv" | "libx265"

       public FFmpegCliEncoder(INativeBridge bridge, string ffmpegPath,
           string encoderName, int width, int height, int bitrateKbps, double fps)
       {
           _bridge = bridge;
           _width = width; _height = height;
           SelectedEncoder = encoderName;

           var startInfo = new ProcessStartInfo(ffmpegPath)
           {
               // -f rawvideo -pix_fmt bgra -s WxH -framerate fps -i - (stdin)
               // -c:v <encoder> -b:v <bitrate>k -f hevc - (stdout)
               Arguments = BuildArguments(encoderName, width, height, bitrateKbps, fps),
               RedirectStandardInput = true,
               RedirectStandardOutput = true,
               UseShellExecute = false, CreateNoWindow = true,
           };
           _ffmpeg = Process.Start(startInfo);
           // Start async stdout reader (background thread collects encoded packets).
       }
   ```
2. `EncodeFrame`:
   a. `_bridge.ReadbackTextureToCpu(texture, out byte[] pixels, out _, out _)`.
   b. `_ffmpeg.StandardInput.BaseStream.Write(pixels)`.
   c. Ler encoded packets do stdout (async, buffering).
   d. Setar `packetBuffer` / `packetSize` do buffer interno.
3. `Flush`:
   a. Fechar stdin (`_ffmpeg.StandardInput.Close()`).
   b. Esperar stdout drain + `WaitForExit()`.
   c. Retornar remaining packets.
4. `Dispose`: matar processo se ainda vivo.
5. **Nota de formato**: sempre `-pix_fmt bgra`. Se readback retornar NV12
   (improvável no caminho quente, mas possível em passthrough sem upscale),
   converter NV12→BGRA no CPU antes de pipear, ou usar `-pix_fmt nv12`
   dinamicamente. **Recomendação**: forçar BGRA sempre (conversão CPU
   trivial: Y*1.164 + Cr/Cb offsets, ou usar swscale do próprio FFmpeg
   adicionando `-vf format=bgra` no filter chain).

### Passo 5 — `EncoderFallbackFactory` (cascata)

1. Criar `src/CATRA.Services/Processing/EncoderFallbackFactory.cs`:
   ```csharp
   internal static class EncoderFallbackFactory
   {
       // Ordem: AMF → FFmpeg(NVENC) → FFmpeg(QSV) → FFmpeg(libx265)
       private static readonly string[] FFmpegHwEncoders = ["hevc_nvenc", "hevc_qsv"];
       private const string SoftwareEncoder = "libx265";

       public static IVideoEncoder Create(
           INativeBridge bridge, string ffmpegPath,
           int width, int height, int bitrateKbps, double fps,
           Action<EncoderFallbackEventArgs>? onFallback = null)
       {
           // 1. Tentar AMF nativo.
           try
           {
               IntPtr ctx = bridge.CreateEncoder(width, height, bitrateKbps, fps);
               return new AmfBridgeEncoder(bridge, ctx);
           }
           catch (NativeBridgeException ex)
               when (ex.ErrorCode is -4 or -2) // CATRA_ERR_DEVICE or CATRA_ERR_NOT_IMPL
           {
               onFallback?.Invoke(new EncoderFallbackEventArgs(
                   "AMF", "FFmpeg cascade",
                   $"AMF unavailable (code={ex.ErrorCode}): {ex.Message}"));
           }

           // 2. Tentar FFmpeg hardware encoders (NVENC → QSV).
           foreach (string hw in FFmpegHwEncoders)
           {
               if (FFmpegCliEncoder.IsEncoderAvailable(ffmpegPath, hw))
               {
                   try
                   {
                       return new FFmpegCliEncoder(bridge, ffmpegPath, hw,
                           width, height, bitrateKbps, fps);
                   }
                   catch
                   {
                       onFallback?.Invoke(new EncoderFallbackEventArgs(
                           hw, "next encoder", $"{hw} init failed"));
                   }
               }
           }

           // 3. Fallback: libx265 software.
           onFallback?.Invoke(new EncoderFallbackEventArgs(
               "hardware encoders", SoftwareEncoder,
               "No hardware encoder available; using software encode"));
           return new FFmpegCliEncoder(bridge, ffmpegPath, SoftwareEncoder,
               width, height, bitrateKbps, fps);
       }
   }
   ```
2. `FFmpegCliEncoder.IsEncoderAvailable(ffmpegPath, encoderName)`:
   - Rodar `ffmpeg -hide_banner -encoders` e grep pelo nome do encoder.
   - Cache do resultado (static `HashSet<string>`) — probe roda uma vez.
   - Se `ffmpeg.exe` não existe → false (fallback direto para libx265
     também falhará; erro será surfacado no `Process.Start`).
3. **Tratamento de erros na cascata FFmpeg**:
   - `Process.Start` falha (ffmpeg não encontrado) → propagar como
     `IOException` com mensagem clara; pipeline converte em `ProcessResult`
     falho (mesmo padrão de hoje para AudioMuxer).
   - Encoder existe mas init falha (GPU incompatível, parâmetros errados)
     → stderr do FFmpeg contém erro; capturar e logar; tentar próximo.
   - Todos falham → `IOException` propagada; pipeline falha com mensagem
     descritiva (melhor que o `NativeBridgeException` de hoje).

### Passo 6 — Integrar no pipeline

1. `ProcessingPipeline.cs` — modificar:
   a. Adicionar campo `Action<EncoderFallbackEventArgs>? _onEncoderFallback`
      (ou evento público `event EventHandler<EncoderFallbackEventArgs>?
      EncoderFallback`). Story 04 assina este evento para exibir na UI.
   b. Linha 267-269 — trocar:
      ```csharp
      // ANTES:
      encodeContext = bridge.CreateEncoder(w, h, bitrate, fps);
      haveEncode = true;
      ```
      ```csharp
      // DEPOIS:
      videoEncoder = EncoderFallbackFactory.Create(
          bridge, _ffmpegPath, w, h, bitrate, fps, args => EncoderFallback?.Invoke(this, args));
      haveEncode = true;
      ```
   c. Frame loop (Stage 3, encoderTask): trocar
      `bridge.EncodeFrame(encodeContext, frame.Texture, ...)` por
      `videoEncoder.EncodeFrame(frame.Texture, ...)`.
   d. Flush: trocar `bridge.FlushEncoder(encodeContext, ...)` por
      `videoEncoder.Flush(...)`.
   e. Finally cleanup: trocar `bridge.DestroyEncoder(encodeContext)` por
      `videoEncoder?.Dispose()`.
   f. Adicionar `SelectedEncoder` ao `ProcessResult` (novo campo opcional).
   g. Log: `[ProcessingPipeline] Encoder selected: {videoEncoder.SelectedEncoder}`.
2. **Resolução do path FFmpeg**: mesmo padrão do `AudioMuxer` —
   `Path.Combine(AppContext.BaseDirectory, "lib", "ffmpeg", "ffmpeg.exe")`
   com fallback para PATH. Constante compartilhada em `AudioMuxer` ou
   utility class.
3. `haveEncode` flag: agora controla `videoEncoder?.Dispose()` em vez de
   `bridge.DestroyEncoder()`.

### Passo 7 — Evento tipado (`EncoderFallbackEventArgs`)

1. Criar `src/CATRA.Core/Processing/EncoderFallbackEventArgs.cs`:
   ```csharp
   public sealed class EncoderFallbackEventArgs : EventArgs
   {
       public string From { get; }      // ex.: "AMF", "hevc_nvenc"
       public string To { get; }        // ex.: "FFmpeg cascade", "libx265"
       public string Reason { get; }    // ex.: "CATRA_ERR_DEVICE (-4): amfrt64.dll not found"
       // ctor com os 3 params
   }
   ```
2. `ProcessingPipeline` expõe `event EventHandler<EncoderFallbackEventArgs>?
   EncoderFallback`.
3. Story 04 (UI) assina este evento — fora do escopo desta subtask.

### Passo 8 — Testes da cascata

1. `tests/CATRA.Services.Tests/Processing/EncoderFallbackFactoryTests.cs`:
   - **AMF succeeds → AmfBridgeEncoder**: mock bridge `CreateEncoder` retorna
     IntPtr válida; verificar tipo é `AmfBridgeEncoder` e `SelectedEncoder == "AMF"`.
   - **AMF ERR_DEVICE → FFmpeg cascade**: mock bridge `CreateEncoder` lança
     `NativeBridgeException(-4)`; mock `FFmpegCliEncoder.IsEncoderAvailable`
     retorna true para "hevc_nvenc"; verificar tipo é `FFmpegCliEncoder`.
   - **AMF ERR_NOT_IMPL → FFmpeg cascade**: mesmo com `-2`.
   - **AMF falha, NVENC unavailable, QSV available → hevc_qsv**: mock
     `IsEncoderAvailable("hevc_nvenc")` → false, `IsEncoderAvailable("hevc_qsv")` → true.
   - **Tudo falha → libx265**: mock todos unavailable exceto libx265.
   - **Fallback events fired**: contar invocações do callback; verificar
     `From`/`To`/`Reason` em cada transição.
   - **AMF falha com outro código (ex: ERR_INIT -1) → NÃO faz fallback**:
     verificar que `ERR_INIT` propaga (não é gatilho de fallback — só
     DEVICE e NOT_IMPL).
2. `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs`:
   - Adicionar caso: `FakeNativeBridge.CreateEncoder` lança
     `NativeBridgeException(-4)` → pipeline não falha, usa encoder fallback
     (injeta mock factory ou fake encoder).
   - **Nota**: `FakeNativeBridge` existente não suporta falha seletiva no
     `CreateEncoder`. Adicionar `ThrowOnCreateEncoder` property (segue
     padrão `ThrowOnEncodeFrame` já existente, linha 314-320).
3. `tests/CATRA.Services.Tests/Processing/FFmpegCliEncoderTests.cs` (opcional,
   se ffmpeg disponível no CI):
   - Encode de N frames de teste (BGRA sólido) → verificar stdout contém
     dados HEVC (NAL units com start code 0x000001).
   - Skip condicional se ffmpeg não disponível (padrão
     `AudioMuxerIntegrationTests`).

## Critérios de Aceite

- [ ] **CA-2.1** — Em máquina AMD com Adrenalin, AMF continua selecionado
      (verificável por log: `[ProcessingPipeline] Encoder selected: AMF`).
      Sem regressão do fluxo atual.
- [ ] **CA-2.2** — `CATRA_ERR_DEVICE` (-4) ou `CATRA_ERR_NOT_IMPL` (-2)
      no `CreateEncoder` AMF dispara fallback para FFmpeg. Job não falha.
- [ ] **CA-2.3** — Export completa com libx265 como último fallback.
      Saída HEVC válida (mux + playback OK).
- [ ] **CA-2.4** — Fluxos funcionam sem `amfrt64.dll`; apenas DLLs
      redistribuídas no bundle.
- [ ] **CA-2.5** — Encoder selecionado e motivo ficam no log do job
      (`Encoder selected: <name>` + fallback events com `Reason`).
- [ ] **CA-2.6** — Testes automatizados cobrem: gatilhos DEVICE/NOT_IMPL,
      ordem de cascata, eventos disparados, outros códigos NÃO fazem fallback.
- [ ] **CA-2.7** — Testes existentes passam (build gate).

## Notas de Implementação

### Riscos identificados

| # | Risco | Mitigação |
|---|-------|-----------|
| R1 | Readback D3D12→CPU adiciona overhead (~1-2ms/frame 1080p) | Só ativo em fallback; caminho AMF inalterado |
| R2 | FFmpeg hevc_nvenc pode falhar silenciosamente (encoder criado mas não produz packets) | Capturar stderr async; timeout por frame (padrão `_frameTimeout` existente) |
| R3 | Processo FFmpeg zombie se pipeline crashar | `Dispose()` mata processo; `finally` no pipeline já limpa contexts |
| R4 | `catra_texture_readback_bgra` em textura D3D11 (sem upscale) | Implementar ambos caminhos (D3D12 + D3D11 via ReadBack11ToCpu) |
| R5 | BGRA → NV12 conversion se FFmpeg encoder exigir NV12 | Usar `-pix_fmt bgra` (FFmpeg aceita e converte internamente) |

### Interação com outras stories

- **Story 01** (FSR4 fix): independente. Esta subtask não altera upscale.
- **Story 03** (FSR3 FG): se FG produzir textura D3D12 no futuro, o
  readback do bridge já serve para o caminho FFmpeg.
- **Story 04** (UI fallback): assina `EncoderFallback` event desta subtask.
  Contrato: `EncoderFallbackEventArgs(From, To, Reason)`.
- **Story 05** (QA): validação em máquina real AMD + simulação não-AMD.

### Caminho mais simples para CA-2.3 (libx265)

Se o desenvolvedor tiver limitação de tempo, o **MVP funcional** é:
1. AMF falha → pular direto para libx265 (sem NVENC/QSV intermediário).
2. Readback BGRA → pipe para `ffmpeg -f rawvideo -pix_fmt bgra -c:v libx265`.
3. Log: "AMF unavailable → using libx265 software encode".
4. NVENC/QSV ficam como follow-up (cascata expandida depois).

Esta abordagem atende CA-2.1 a CA-2.7 e é significativamente mais simples.
A cascata completa (com hw encoders) pode ser feita como refinement.

### `FakeNativeBridge` — extensão necessária

Adicionar property para simular falha no CreateEncoder:
```csharp
public NativeBridgeException? ThrowOnCreateEncoder { get; set; }

public IntPtr CreateEncoder(int w, int h, int br, double fps)
{
    if (ThrowOnCreateEncoder is not null) throw ThrowOnCreateEncoder;
    // ... existing code
}
```
Segue o padrão `ThrowOnEncodeFrame` (linha ~326 dos testes existentes).

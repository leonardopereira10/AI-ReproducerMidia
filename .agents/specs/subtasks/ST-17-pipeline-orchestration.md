# Subtask 17: Pipeline Orchestration C#

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Pipeline Orchestration, RF-03, RN-07)
Depende de ST-13 (RIFE), ST-14 (FSR 4), ST-15 (interop), ST-16 (AMF).
**Complexidade: CRITICAL** — integra todos os componentes GPU.

## Objetivo
Serviço C# que orquestra o pipeline completo de pre-processamento:
FFmpeg decode → RIFE interp → FSR 4 upscale → AMF encode → FFmpeg mux.
Gerencia lifecycle dos contextos nativos, reporta progresso, suporta cancelamento.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Processing/ProcessingPipeline.cs` — orquestrador principal
- `src/CATRA.Services/Processing/PipelineConfig.cs` — config por perfil
- `src/CATRA.Services/Processing/PipelineProgress.cs` — model de progresso
- `src/CATRA.Services/Processing/PipelineStep.cs` — enum: Decode, Interp, Upscale, Encode, Mux
- `src/CATRA.Services/Processing/FrameDecoder.cs` — FFmpeg decode → texturas GPU
- `src/CATRA.Services/Processing/AudioMuxer.cs` — extrai audio + muxa no output
- `src/CATRA.Core/Interfaces/IProcessingPipeline.cs`
- `tests/CATRA.Services.Tests/Processing/ProcessingPipelineTests.cs`

### Arquivos a Modificar
- `src/CATRA.Services/Processing/NativeBridge.cs` — garantir API completa
- `src/CATRA.App/App.xaml.cs` — registrar IProcessingPipeline

### Arquivos NÃO tocar
- `native/catra-gpu/` — não modificar (ST-12..16 prontos)
- `src/CATRA.UI/` — sem UI (ST-19)

## Requisitos Técnicos

### IProcessingPipeline
```csharp
public interface IProcessingPipeline
{
    Task<ProcessResult> ProcessAsync(
        Episode episode,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);

    Task<ProcessResult> ProcessBatchAsync(
        List<Episode> episodes,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);
}

public record PipelineConfig(
    ProcessProfile Profile,      // Local | Dlna
    int TargetWidth,
    int TargetHeight,
    double TargetFps,
    int EncodeBitrateKbps,
    string InterpMethod,         // "rife" | "fsr3fg"
    string UpscaleMethod,        // "fsr4" | "fsr1"
    string OutputFolder);

public record PipelineProgress(
    int EpisodeIndex,            // qual episódio no batch
    int EpisodeCount,
    PipelineStep CurrentStep,
    double StepProgressPct,      // 0-100 dentro da etapa
    double OverallPct,           // 0-100 total
    TimeSpan Elapsed,
    TimeSpan? Eta);

public record ProcessResult(
    bool Success,
    string? OutputPath,
    long OutputSizeBytes,
    TimeSpan Duration,
    string? ErrorMessage);
```

### ProcessingPipeline.ProcessAsync
Fluxo conforme spec (Pipeline Orchestration):

```
1. Open source → FFmpeg avformat_open_input
   - Read metadata: fps, resolution, duration, audio streams
   - Calcular total frames = duration * src_fps

2. Decide steps (RN-07):
   - needInterp = src_fps < target_fps
   - needUpscale = src_height < target_height
   - Log: "Pipeline: interp={needInterp}, upscale={needUpscale}"

3. Init native contexts:
   - catra_interp_create(src_w, src_h, src_fps, target_fps, method)
   - catra_upscale_create(src_w, src_h, dst_w, dst_h, method)
   - catra_encode_create(dst_w, dst_h, bitrate, target_fps)

4. Open output: FFmpeg avformat_alloc_output_context2("mp4")
   - Add video stream: AV_CODEC_ID_HEVC, dst_w×dst_h, target_fps
   - avio_open → output file

5. Frame loop:
   frameIndex = 0
   WHILE av_read_frame → packet:
     avcodec_send_packet → avcodec_receive_frame → AVFrame (D3D11 texture)

     IF needInterp:
       catra_interp_process(frameA, frameB) → interpFrames[]
       FOR each interpFrame:
         IF needUpscale: catra_upscale_process(frame) → upscaled
         ELSE: upscaled = frame
         catra_encode_frame(upscaled) → nalData
         IF nalData: write to output
     ELSE:
       IF needUpscale: catra_upscale_process(frame) → upscaled
       ELSE: upscaled = frame
       catra_encode_frame(upscaled) → nalData
       IF nalData: write to output

     frameIndex++
     progress.Report(current step, frameIndex / totalFrames)

     IF cancellationToken.IsCancellationRequested: BREAK

6. Flush encoder: catra_encode_flush → remaining NALs → write

7. Mux audio (AudioMuxer):
   - Decode audio stream do source (FFmpeg)
   - Encode AAC (ou copy se já AAC)
   - Mux into output file
   - Nota: pode ser feito em passo separado (2-pass) para simplificar

8. Cleanup: destroy todos os contextos nativos
9. Return ProcessResult
```

### FrameDecoder
- Wrapper FFmpeg.AutoGen para decode no pipeline (separado do PlaybackEngine)
- D3D11VA hw accel (mesmo setup da ST-05)
- Output: `ID3D11Texture2D*` por frame
- Gerencia frame pairs para interp (frame A + frame B)
- Seek: não necessário no pipeline (decode sequencial)

### AudioMuxer
- Extrair audio do source: `av_read_frame` no audio stream
- Se codec = AAC → stream copy (sem re-encode)
- Se codec != AAC → decode + encode AAC (FFmpeg `AV_CODEC_ID_AAC`)
- Muxar no output MP4: adicionar audio stream + escrever packets
- Timing: audio deve bater com vídeo (mesma duração)
- Abordagem 2-pass: primeiro encode vídeo, depois mux audio
  - Mais simples que interleaving em tempo real

### Progress e ETA
- Progress por etapa com peso:
  - Decode: 10% (overlapped com interp/upscale)
  - Interp: 35% (se ativo)
  - Upscale: 35% (se ativo)
  - Encode: 15%
  - Mux: 5%
- ETA: baseado em frames processados / tempo decorrido
- Report a cada 100 frames (não a cada frame — overhead)

### Cancelamento
- `CancellationToken` checado no frame loop
- Ao cancelar: flush encoder (parcial), cleanup contexts, deletar output parcial
- `ProcessResult.Success = false`, `ErrorMessage = "Cancelled"`

### Error Handling
- FFmpeg error → log + ProcessResult com erro
- Native bridge error (retorno < 0) → NativeBridgeException → log + abort
- Out of disk space → IOException → log + abort
- Timeout por frame: se frame demora > 30s → abort (GPU hang)

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Pipeline processa MP4 720p 24fps → 1080p 135fps (perfil local)
- [ ] Pipeline processa MP4 720p 24fps → 4K 55fps (perfil dlna)
- [ ] Output é MP4 H.265 válido (toca em ffplay/VLC)
- [ ] Audio presente e sincronizado no output
- [ ] Progress reportado corretamente (step + %)
- [ ] ETA razoável (±30% do real)
- [ ] Cancelamento funciona (sem crash, cleanup OK)
- [ ] RN-07: skip interp se fps >= target, skip upscale se res >= target
- [ ] Batch: processa múltiplos episódios sequencialmente
- [ ] Sem leak de GPU memory após batch de 5 episódios
- [ ] Erro em um episódio não aborta batch inteiro

## Dependências
- ST-13 (RIFE interp)
- ST-14 (FSR 4 upscale)
- ST-15 (D3D11↔DX12 interop)
- ST-16 (AMF encode)
- ST-05 (FFmpeg.AutoGen setup, D3D11VA)

## Notas
- Esta é a subtask de integração mais complexa do projeto
- Testar incrementalmente:
  1. Decode only → salvar frames como PNG (validar decode)
  2. Decode + interp → salvar frames (validar RIFE)
  3. Decode + upscale → salvar frames (validar FSR)
  4. Decode + encode → validar H.265 output
  5. Pipeline completo → validar MP4 final
- Audio mux 2-pass simplifica muito vs interleaving real-time
- Frame pairs para RIFE: manter frame anterior + atual
  - Último frame: duplicar (sem par) ou skip
- Progress weights são estimativas — ajustar com dados reais

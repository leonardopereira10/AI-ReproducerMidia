# Subtask 05: FFmpeg Decode D3D11VA + Render DX11 SwapChain

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-05, Integração FFmpeg)
Depende de ST-01 (estrutura). Subtask mais crítica da Fase 1.

## Objetivo
Pipeline de reprodução local: FFmpeg.AutoGen decodifica vídeo com hardware
acceleration (D3D11VA), renderiza via DX11 SwapChain embedado em WPF via
HwndHost. Audio decode + WASAPI output. Suporte a MP4, AVI, MKV.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Playback/VideoDecoder.cs` — FFmpeg decode wrapper
- `src/CATRA.Services/Playback/AudioDecoder.cs` — FFmpeg audio decode
- `src/CATRA.Services/Playback/AudioRenderer.cs` — WASAPI output (NAudio)
- `src/CATRA.Services/Playback/VideoRenderer.cs` — DX11 SwapChain → HwndHost
- `src/CATRA.Services/Playback/PlaybackEngine.cs` — orquestra decode+render+audio
- `src/CATRA.Services/Playback/PlaybackState.cs` — enum: Stopped, Playing, Paused, Seeking
- `src/CATRA.Services/Playback/Clock.cs` — A/V sync via stopwatch
- `src/CATRA.Core/Interfaces/IPlaybackEngine.cs`
- `src/CATRA.Core/Interfaces/IVideoDecoder.cs`
- `src/CATRA.Core/Interfaces/IVideoRenderer.cs`
- `src/CATRA.Core/Interfaces/IAudioRenderer.cs`
- `src/CATRA.Core/Models/VideoMetadata.cs` — duration, fps, width, height, codec
- `src/CATRA.Core/Models/AudioTrack.cs` — index, language, codec
- `src/CATRA.UI/Controls/VideoHostControl.xaml` + `.cs` — HwndHost wrapper
- `lib/ffmpeg/` — FFmpeg shared libraries (avcodec, avformat, avutil, swscale, swresample)
- `tests/CATRA.Services.Tests/Playback/PlaybackEngineTests.cs`

### Arquivos a Modificar
- `src/CATRA.App/App.xaml.cs` — registrar playback services no DI
- `src/CATRA.App/CATRA.App.csproj` — adicionar referência FFmpeg.AutoGen, NAudio

### Arquivos NÃO tocar
- `src/CATRA.Data/` — sem DB
- `src/CATRA.Services/Library/` — sem scanner

## Requisitos Técnicos

### VideoDecoder (FFmpeg.AutoGen)
- `avformat_open_input` → `avformat_find_stream_info` → selecionar video stream
- `avcodec_find_decoder` → criar `AVCodecContext` com `hw_device_ctx` D3D11VA
- HW accel: `av_hwdevice_ctx_create(AV_HWDEVICE_TYPE_D3D11VA)`
- Decode loop: `av_read_frame` → `avcodec_send_packet` → `avcodec_receive_frame`
- Output: `AVFrame` com `format = AV_PIX_FMT_D3D11` → `data[0]` = `ID3D11Texture2D*`
- Seek: `av_seek_frame` + `avcodec_flush_buffers`
- Metadata: extrair duration, fps, width, height, codec name
- Audio tracks: enumerar streams de áudio (para MKV multi-audio)
- IDisposable: liberar AVFormatContext, AVCodecContext, hw device

### VideoRenderer (DX11 SwapChain)
- Criar `IDXGISwapChain` bound ao HWND do HwndHost
- Render: copiar `ID3D11Texture2D` do decoder → backbuffer → `Present`
- Suporte a resize (recreate swapchain on WM_SIZE)
- VSync: `Present(1, 0)` para sync com monitor
- Clear com cor preta antes do primeiro frame

### AudioRenderer (WASAPI via NAudio)
- `WasapiOut` ou `WasapiOutRT` para output
- Formato: float 32-bit, sample rate do source (48kHz típico)
- Buffer: ~200ms para evitar glitches
- Volume: 0.0–1.0 via `ISimpleAudioVolume` ou NAudio Volume
- Pause/Resume: `WasapiOut.Pause()` / `Play()`

### PlaybackEngine (Orquestrador)
- Thread de decode dedicada (não bloqueia UI)
- A/V sync: `Clock` baseado em `Stopwatch` — audio é master clock
- Frame queue: decoder enfileira frames, renderer consome no ritmo do clock
- Estados: Stopped → Playing ↔ Paused → Stopped
- Seek: pausa → flush decoder → seek → resume
- Eventos: `PositionChanged`, `StateChanged`, `MediaEnded`, `Error`
- `OpenAsync(filePath)` → retorna `VideoMetadata`
- `Play()`, `Pause()`, `Stop()`, `Seek(TimeSpan)`, `SetVolume(float)`
- `GetPosition()` → TimeSpan atual
- Timer de posição: reportar a cada 250ms para UI

### VideoHostControl (WPF)
- Herda `HwndHost`
- `BuildWindowCore` → cria HWND child
- `DestroyWindowCore` → cleanup
- Expõe `IntPtr Handle` para o VideoRenderer criar o SwapChain
- Handle DPI scaling

### FFmpeg Libraries
- Bundled em `lib/ffmpeg/` (shared build, GPL ou LGPL)
- DLLs: avcodec-61.dll, avformat-61.dll, avutil-59.dll, swscale-8.dll, swresample-5.dll
- FFmpeg.AutoGen via NuGet: `FFmpeg.AutoGen`
- `ffmpeg.RootPath` configurado no startup para `lib/ffmpeg/`

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Reproduz MP4 1080p com hw decode (D3D11VA confirmado via log)
- [ ] Reproduz AVI e MKV
- [ ] Vídeo renderiza embedado na WPF (não janela externa)
- [ ] Audio sincronizado com vídeo (sem drift perceptível)
- [ ] Play/Pause/Stop funcionam
- [ ] Seek funciona (forward e backward)
- [ ] Volume control funciona
- [ ] Resize da janela não corrompe render
- [ ] Metadata extraída corretamente (duration, fps, resolution)
- [ ] Dispose limpa recursos (sem leak de GPU memory)
- [ ] CPU < 10% durante playback 1080p (hw decode ativo)

## Dependências
- ST-01 (estrutura de camadas)

## Notas
- Esta é a subtask de maior risco técnico da Fase 1
- FFmpeg.AutoGen usa `unsafe` — habilitar `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
- D3D11VA requer `SharpDX` ou `Vortice.Windows` para interop COM do D3D11
  - Recomendação: `Vortice.Windows` (sucessor moderno do SharpDX)
- Se D3D11VA falhar → fallback para decode software (AV_PIX_FMT_YUV420P + sws_scale)
- A/V sync é notoriamente difícil — começar com audio master clock, refinar depois
- MKV com múltiplos áudios: expor lista de AudioTrack, permitir seleção
- Não implementar legendas nesta subtask (Fase 3)

# Subtask 16: AMF H.265 Encoder

## Contexto
Spec: `.agents/specs/catra-media-player.md` (GPU Encode, RF-03)
Depende de ST-12 (native bridge skeleton).

## Objetivo
Implementar encode H.265 via AMF (AMD Media Framework) na native bridge.
Input: textura GPU (DX12) → output: NAL units H.265. Usado pelo pipeline
de pre-processamento para gerar o arquivo final.

## Escopo
### Arquivos a Criar
- `native/catra-gpu/encode_amf.cpp` — implementação AMF
- `native/catra-gpu/encode_amf.h`
- `lib/amf/` — AMF SDK headers (GPUOpen)
- `tests/native/test_encode.cpp` — teste nativo standalone

### Arquivos a Modificar
- `native/catra-gpu/catra_gpu.cpp` — implementar `catra_encode_*`
- `native/catra-gpu/CMakeLists.txt` — adicionar AMF includes
- `src/CATRA.Services/Processing/NativeBridge.cs` — expor encode methods

### Arquivos NÃO tocar
- `native/catra-gpu/interp_rife.cpp` — ST-13
- `native/catra-gpu/upscale_fsr4.cpp` — ST-14

## Requisitos Técnicos

### AMF SDK
- Source: https://github.com/GPUOpen-LibrariesAndSDKs/AMF
- Headers only (AMF é parte do driver AMD, runtime já instalado)
- `AMFFactory` → `AMFContext` → `AMFComponent("AMFVideoEncoder_HEVC")`
- Input: `AMFSurface` com `AMF_SURFACE_RGBA` ou `AMF_SURFACE_NV12`
- Output: `AMFBuffer` com NAL units H.265

### catra_encode_create
```cpp
int catra_encode_create(int width, int height, int bitrate_kbps,
                        double fps, int* out_ctx)
```
- Criar `AMFContext`, init com D3D12 device (do interop)
- Criar componente `AMFVideoEncoderUVD_H265_MAIN`
- Properties:
  - `AMF_VIDEO_ENCODER_HEVC_FRAMESIZE`: {width, height}
  - `AMF_VIDEO_ENCODER_HEVC_FRAMERATE`: {fps_num, fps_den}
  - `AMF_VIDEO_ENCODER_HEVC_TARGET_BITRATE`: bitrate_kbps * 1000
  - `AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET`: `AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET_QUALITY`
  - `AMF_VIDEO_ENCODER_HEVC_USAGE`: `AMF_VIDEO_ENCODER_HEVC_USAGE_TRANSCONDING` (offline)
  - `AMF_VIDEO_ENCODER_HEVC_PROFILE`: Main
  - `AMF_VIDEO_ENCODER_HEVC_TIER`: Main (até 4K30) ou High (4K55)
- `Init(width, height)` no componente
- Alocar pool de AMFSurface (4-8 surfaces)

### catra_encode_frame
```cpp
int catra_encode_frame(int ctx, void* texture, uint8_t** out_buf, int* out_size)
```
- Input: `ID3D12Resource*` (textura upscaled do FSR 4)
- Wrap em `AMFSurface` (AMF suporta D3D12 surfaces nativamente)
- `SubmitInput(surface)` → `QueryOutput(buffer)`
- Output: `AMFBuffer` → copiar para `uint8_t*` (caller libera)
- `out_size`: tamanho do NAL unit(s)
- Pode retornar 0 bytes (encoder buffering) — caller deve lidar
- Async: submit não bloqueia, query pode retornar vazio

### catra_encode_flush
```cpp
int catra_encode_flush(int ctx, uint8_t** out_buf, int* out_size)
```
- `Drain()` no componente
- Coletar todos os frames restantes no buffer
- Retornar NAL units finais
- Chamado uma vez no final do encode

### catra_encode_destroy
- `Terminate()` no componente e contexto
- Liberar surfaces, buffers

### Muxing (C# side)
- NAL units do AMF → FFmpeg `av_write_frame` para MP4 container
- Pipeline orchestration (ST-17) faz o mux
- Codec parameters: `AV_CODEC_ID_HEVC`, extradata do SPS/PPS
- AMF output: Annex B format → converter para length-prefixed (MP4 requer)
  - Ou usar `h264_mp4toannexb` / `hevc_mp4toannexb` bitstream filter do FFmpeg

### Performance
- AMF H.265 encode 4K55: ~2-5ms por frame (hw encoder dedicado)
- Não compete com compute (FSR 4) — encoder é bloco separado na GPU
- Throughput: > 100fps 4K (muito acima do necessário)

## Critérios de Sucesso
- [ ] `cmake --build` compila com AMF headers
- [ ] AMF context inicializa na RX 9070 XT
- [ ] Encode 1080p135 H.265 funcional (NAL units válidos)
- [ ] Encode 4K55 H.265 funcional
- [ ] Bitrate configurável (20Mbps, 45Mbps testados)
- [ ] Flush retorna frames restantes corretamente
- [ ] Output muxado em MP4 via FFmpeg (testar com ffplay)
- [ ] P/Invoke do C# funciona (NativeBridge.EncodeCreate/Frame/Flush/Destroy)
- [ ] Sem leak após encode de 1000 frames
- [ ] Qualidade visual aceitável (sem blocking/banding em bitrate alvo)

## Dependências
- ST-12 (native bridge skeleton)
- ST-15 (D3D11↔DX12 interop — texturas DX12 como input)

## Notas
- AMF SDK é headers-only — runtime vem do driver AMD (Adrenalin)
- Requer driver AMD recente (24.x+) para H.265 encode em RDNA 4
- `AMF_VIDEO_ENCODER_HEVC_USAGE_TRANSCONDING` é otimizado para offline
  (melhor qualidade, mais latência — aceitável)
- Alternativa: FFmpeg com AMF wrapper (`h265_amf` encoder)
  - Mais simples mas menos controle
  - Considerar se AMF direto for muito complexo
- Annex B → MP4: usar `av_bsf_send_packet` com `hevc_mp4toannexb` filter
- Testar: arquivo output deve tocar em Samsung TV (validação DLNA na ST-20)

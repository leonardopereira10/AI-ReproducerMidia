# Prompt: Continuar Fix Frames Verdes (DXGI/AMD RDNA4)

> Cole este conteúdo como primeira mensagem em uma nova sessão do Pi.

---

## PROMPT

```
🐛 fix bug — frames VERDES no MP4 processado (driver AMD RDNA 4)

Leia PRIMEIRO, nesta ordem:
1. `.agents/sprint_atual/SPRINT_LOG.md` (histórico + eventos da investigação)
2. `.agents/specs/subtasks/PROMPT_CONTINUE_DEVICE_REMOVED.md` (contexto do pipeline)
3. `native/catra-gpu/tools/interop_readback_test.cpp` (ferramenta de diagnóstico)
CWD: C:/Projetos/Reprodutor_CATRA

## PIPELINE
FFmpeg decode (D3D11VA, NV12) → RIFE interp (DirectML/D3D11) → interop D3D11→D3D12
→ AMF HEVC encode (D3D12) → mux MP4 (ffmpeg CLI). Output: D:\MediaPlayer\.cache\{id}_local.mp4

## CADEIA DE CAUSAS RAIZ JÁ IDENTIFICADAS (driver AMD RDNA 4 — TODAS específicas dele)
1. DXGI_ERROR_DEVICE_REMOVED no encode → corrigido (commit ab9459a): AcquireSync
   retorna WAIT_TIMEOUT cru (0x102); key≠0 rejeitada em mutex fresco; falta Flush.
2. Mux falhou "ffmpeg binary not available" → corrigido (9cf7da1): bundlar ffmpeg/ffprobe.
3. Share NV12 D3D11→D3D12 lê zeros → unificado em BGRA (aee1166).
4. **CAUSA REAL DO VERDE**: `ID3D11DeviceContext::CopySubresourceRegion()` lendo de
   fonte NV12 (planar) retorna ZEROS. Corrigido no RIFE (TextureToTensor) e no interop
   (ConvertNv12ToBgra11) via CopyResource+Map(sub0) contíguo (commit 9a53d78), que
   também trocou o encode p/ pool D3D12-nativo por round-trip CPU (lento mas correto).
5. **FONTE ORIGINAL DO VERDE (AINDA ABERTA)**: `FrameDecoder.CopyToStandaloneTexture`
   (C#) usa CopySubresourceRegion p/ copiar a fatia do array D3D11VA → frames decodificados
   nascem verdes. REESCRIVI o método (readback CPU staging+Map → NV12→BGRA → standalone
   BGRA) MAS **NÃO compilei/testei ainda** — está só no working tree.

## COMMITS JÁ FEITOS
ab9459a (DEVICE_REMOVED) · 9cf7da1 (mux) · aee1166 (BGRA) · 9a53d78 (CopySubresourceRegion
RIFE/interop + pool CPU) · 818982e/0e0757b (docs)

## ESTADO ATUAL
- `src/CATRA.Services/Processing/FrameDecoder.cs` MODIFICADO (não commitado, não testado).
- App pode estar rodando/processando EP28 com DLL antiga (ainda verde pq decoder verde).
- Round-trip CPU do encode é LENTO (~2-3MB/h → episódio ~10h). Corretude > velocidade por ora.

## PRÓXIMOS PASSOS (na ordem)
1. `dotnet build CATRA.sln` — corrija erros de compile do FrameDecoder.cs (API Vortice:
   `Map(tex, sub, MapMode.Read, MapFlags.None)` retorna DataBox c/ .DataPointer/.RowPitch;
   `Unmap(tex, sub)`; precisa bloco `unsafe`; `System.Buffer.MemoryCopy`). O método deve
   emitir textura BGRA standalone (ArraySize=1), NÃO NV12.
2. Matar CATRA.App (`taskkill /IM CATRA.App.exe /F`), `dotnet build`, e garantir que a DLL
   nativa em `src/CATRA.App/bin/Debug/net8.0-windows/runtimes/win-x64/native/catra-gpu.dll`
   = a de `native/catra-gpu/install/...` (md5).
3. `dotnet test` (542 testes) deve seguir verde.
4. Commit do FrameDecoder.cs.
5. Rodar app, iniciar pré-processamento do EP28 (série "A Record of a Mortal's Journey",
   rádio Local, botão "📥 Iniciar").
6. VALIDAR CEDO (não espere terminar): decodifique o `.tmp` parcial e cheque que NÃO é verde:
   `ffmpeg -i "D:\MediaPlayer\.cache\53_local.mp4.video.tmp" -vframes 1 out.png`
   → PNG de frame com conteúdo real > 50KB; frame sólido/verde ≈ 8-9KB. Se verde, continue
   depurando o decoder (imprima bytes Y/UV no readback p/ confirmar).
7. Se o decode sair correto, deixe o episódio terminar (ou valide vários frames) e confirme o
   MP4 final com conteúdo (3 frames em 30/300/900s, todos > 50KB) + ffprobe (hevc 135fps + aac).

## OPCIONAL — VELOCIDADE (depois que o verde estiver 100% corrigido)
O round-trip CPU do encode é gargalo. Como agora TODO o pipeline é BGRA (decoder emite BGRA,
RIFE emite BGRA), teste restaurar o caminho GPU compartilhado rápido no interop
(`EnsurePoolLocked` + CopyResource + Flush + WaitForD3D11GpuIdle, veja git show aee1166) no
lugar do round-trip CPU (`EnsurePool12Locked`/`ReadBack11ToCpu`/`UploadCpuTo12`). Valide com o
diagnóstico `catra-interop-test.exe` (dump RIFE→interop→AMF deve decodificar p/ pattern, não
verde) ANTES de aceitar. Se bandar/verdar sob DirectML, MANTENHA o round-trip CPU.

## FERRAMENTAS / AMBIENTE
- Build nativo: `build_native_now.bat` (MSVC). Teste nativo: `native/catra-gpu/build/Release/catra-interop-test.exe`
  (sete `CATRA_RIFE_MODEL_PATH=C:\Projetos\Reprodutor_CATRA\lib\rife\rife_v4.onnx`;
  `CATRA_INTEROP_DEBUG=1` p/ prints).
- ffmpeg/ffprobe: `src/CATRA.App/bin/Debug/net8.0-windows/lib/ffmpeg\{ffmpeg,ffprobe}.exe`
- DB: `C:\Users\stizt\AppData\Roaming\CATRA\catra.db` (tabela ProcessJob; colunas
  Status/ProgressPct/CurrentStep/ErrorMessage).
- NÃO tocar ST-20/21/22. Arquivos pré-modificados não relacionados: VideoDecoder.cs,
  VideoRenderer.cs, PlayerViewModel.cs, .agents/agents/*.md (deixe como estão).

## DELEGAÇÃO / RATE LIMIT
Use subagents p/ monitoramento longo (async). Se o modelo principal cair em rate limit,
relance o subagent com override `model: "qwen-ai/qwen3.7-plus"` (ou deepseek-v4-pro p/ análise).

## CRITÉRIOS DE ACEITE
- [ ] FrameDecoder não usa CopySubresourceRegion p/ NV12; emite BGRA limpo.
- [ ] `.tmp` parcial decodifica p/ frames com conteúdo (> 50KB), não verde.
- [ ] MP4 final do EP28 com conteúdo real (3 frames > 50KB) + ffprobe hevc 135fps + aac.
- [ ] dotnet build 0w/0e, dotnet test 542/542.
- [ ] Commits feitos (FrameDecoder + qualquer fix adicional).
```

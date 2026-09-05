# Spike — FSR Frame Generation offline (subtask 03, Story 03)

**Veredito: NO-GO** — FFX Frame Generation fora do swapchain de playback
(dispatch offscreen direto) **não produz frames gerados** no bundle FFX 2.3.0
com o provider disponível na máquina dev. O runtime aceita todo o fluxo
(create/configure/dispatch, ret=OK) mas degrada silenciosamente para
**passthrough**: `outputs[0]` é uma cópia idêntica do `presentColor` atual,
em 100% dos dispatches, em todas as configurações testadas.

| Campo | Valor |
|---|---|
| Data | 2026-02 (execuções v2 e v3, métricas byte-idênticas) |
| Máquina | AMD Radeon RX 9070 XT (vendor=0x1002 device=0x7550), RDNA 4 |
| Bundle | FidelityFX SDK 2.3.0 (`lib/`), loader `amd_fidelityfx_loader_dx12.dll`, 8/8 DLLs presentes |
| Providers FG enumerados | **4.0.1** (id=0xf600000001000001) e **3.1.6** (id=0xf600000000c01006) |
| Ferramenta | `native/catra-gpu/tools/fg_offline_smoke_test.cpp` (target CMake `catra-fg-offline-test`) |
| Logs | `native/catra-gpu/build/Release/fg_offline_run_v2.log`, `fg_offline_run_v3.log` |
| Evidência visual | `fgspike_*_source_off80.bmp` / `fgspike_*_generated_off80.bmp` no build dir |

## Metodologia

- Fonte sintética 640×360: gradiente + blob deslocado **16 px/frame**
  (movimento unidirecional conhecido, ground-truth exato).
- Métrica `t`: 0 = saída idêntica ao present atual (**passthrough**),
  0.5 = **interpolação genuína** (meio caminho entre present anterior e atual),
  1 = cópia do present anterior. Calculada por NCC (normalized
  cross-correlation) sobre o frame inteiro + tracking independente do blob.
- 12 dispatches por fase (2 warmup + 10 medidos), `frameID` +1 por dispatch,
  readback staging + análise CPU. Dump BMP no offset 80 para inspeção visual.
- 10 fases variando: Prepare dispatch (nenhum / MV negativo / MV positivo),
  formato fonte (BGRA / RGBA), flags de Configure (debug view), versão do
  provider (4.0.1 / 3.1.6 pinado), omissão da flag
  `NO_SWAPCHAIN_CONTEXT_NOTIFY`, e `numGeneratedFrames=2`.

## Evidência por incógnita

### U1 — Configure com `swapChain = nullptr`: **PASS**

```
U1 CreateContext(FG 640x360, no swapchain) ret=0 (OK)
U1 Configure(swapChain=nullptr, enabled, NO_SWAPCHAIN_CONTEXT_NOTIFY) ret=0 (OK)
U1 teardown (Configure disable + DestroyContext) OK
```

- O provider **4.0.1** aceita contexto FG sem swapchain real. Não foi preciso
  o fallback de HWND oculto + swapchain dummy.
- Probe complementar: **sem** a flag `NO_SWAPCHAIN_CONTEXT_NOTIFY`,
  `Configure(swapChain=nullptr)` é rejeitado com `ret=3`
  (`FFX_API_RETURN_ERROR_RUNTIME_ERROR`) — a flag é **obrigatória** nesse modo.
- Provider **3.1.6** pinado (`FFX_FRAMEGENERATION_MAKE_VERSION(3,1,6)` =
  0x00C01006, encoding verificado no header): `CreateContext ret=6`
  (`FFX_API_RETURN_ERROR_PARAMETER`) — o provider FSR 3.1.6 **recusa criação
  de contexto FG sem swapchain** nesta máquina/bundle.

### U2 — Dispatch offscreen produz frames interpolados: **FAIL (gate derrubado)**

Resultado idêntico em **todas as 7 fases que executaram** (noprep-bgra,
prep-mvneg-bgra, prep-mvpos-bgra, noprep-rgba, prep-mvneg-rgba,
noprep-bgra-debugview, noprep-bgra-2gen):

```
SUMMARY dispatches=12 executedOk=12 measured=10 | out[0]:
  interpHits=0 passthroughHits=10 prevCopyHits=0
  meanT(ncc)=+0.000 meanNcc=1.000 swapVotes=0 mirrorVotes=0
VERDICT: no interpolation detected
```

- `t=+0.000` com `ncc=1.000`: a saída é **cópia exata do presentColor atual**
  em 10/10 frames medidos × 7 fases. Zero frames gerados.
- **Prepare com motion vectors (±sinal) não teve efeito algum** — as métricas
  das fases `prep-*` são byte-idênticas às `noprep-*`. O internal optical flow
  também não dispara: o runtime nunca gera sem o caminho de Present.
- `numGeneratedFrames=2`: `out[1]` nunca é escrito (preto,
  `nonBlack=0.00`, readback não preenchido) — só `out[0]` recebe o passthrough.
- Flag `DRAW_DEBUG_VIEW` (0x4): aceita, sem mudança de comportamento.

**Evidência de controle (decisiva):** na **mesma máquina**, o caminho
**playback** (`fg_smoke_test`: swapchain FG real + Present, 90 frames
submetidos) gera frames normalmente — `generated=90, ratio=2.00`
(execução anterior registrada no handoff da subtask). Ou seja, FG funciona;
o que falha é especificamente o modo **offscreen sem Present**.

**Root cause (conclusão do spike):** no provider FG 4.0.1 (FSR 4 FG, RDNA 4),
a geração de frames é **gated pelo caminho de present/pacing do swapchain FG**.
Com `NO_SWAPCHAIN_CONTEXT_NOTIFY` o runtime executa o dispatch sem erro, mas
não havendo Present iminente, retorna o frame fonte (passthrough) como
comportamento definido — não é bug de uso nosso: MV Prepare, formatos,
flags, frameID e resource states foram todos exercitados sem efeito. O
provider alternativo 3.1.6 (era FSR 3, em que o sample oficial exercita
dispatch direto offscreen) recusa a criação do contexto sem swapchain
(ret=6), fechando a segunda rota.

### U3 — Resource states / barreiras de `outputs[]`: **PASS**

```
U3 probe [UNORDERED_ACCESS-declared] #0..#2 ret=0 nonBlack=1.00
U3 scheme UNORDERED_ACCESS-declared: 3/3 dispatch executed, 3/3 non-black
U3 chosen scheme: UNORDERED_ACCESS-declared
```

Textura destino criada com `ALLOW_UNORDERED_ACCESS` e mantida em
`UNORDERED_ACCESS` funciona; o runtime faz as transições internas. Não é
bloqueio (o passthrough de U2 prova que o dispatch escreve no destino).

### U4 — Fidelidade de formato/canais: **INDETERMINADA (moot sob NO-GO)**

`blobChan=R10 G0 B0`, `swapVotes=0`, `mirrorVotes=0` — mas como a saída é
cópia idêntica da fonte, não há frame *gerado* para avaliar R↔B. Registro
para o futuro: saída FG é BGRA; o encoder AMF consome NV12 e **não existe**
conversor BGRA→NV12 no repo (só NV12→BGRA, `nv12_to_bgra_shader.cpp`). Um
GO futuro exigiria novo compute shader ou codificação BGRA.

### U5 — Contrato `frameID`: **PASS com ressalva**

```
U5 gap dispatch (frameID 3 -> 5, gap=+2): ret=0 nonBlack=1.00
U5 backwards dispatch (frameID 10 -> 7): ret=0 executed=1 gpuOk=1
U5 big-jump dispatch (frameID 8 -> 100008): ret=0 executed=1 gpuOk=1
U5 post-gap recovery: 4/4 valid dispatches (no error surfaced)
```

Violações de contrato (gap, regressão, salto +100k) são **silenciosamente
toleradas** — ret=OK, sem device loss. O header documenta que qualquer
diferença ≠ +1 **reseta a lógica de FG** sem sinalizar erro. Um backend
offline teria que garantir `frameID` +1 exato **por contrato próprio**
(contador interno), nunca confiar no runtime para detectar.

### U6 — Custo por frame @1920×1080: **MEDIDO**

```
U6 GPU memory @1080p: total=87 MB aliasable=66 MB
U6 @1920x1080: n=30 avg=23.92 ms median=21.54 ms min=21.03 ms max=45.93 ms
  (~42 frames/s end-to-end, incluindo upload da fonte + readback + análise CPU)
```

(v2: avg=22.56/median=19.67 — reprodutível.) **Atenção:** este é o custo do
dispatch em **passthrough** — piso da maquinaria FG, não de geração real. Com
optical flow + geração ativos o custo seria maior. Referência: RIFE via
ONNX/DirectML já entrega interpolação real no pipeline offline; mesmo um FG
offscreen funcional não teria vantagem clara de custo.

## Known issues menores (não-bloqueantes)

- `Configure(GlobalDebug log bridge)` falha com `ret=2`
  (`UNKNOWN_DESCTYPE`) em ambos desctypes — log bridge indisponível neste
  provider; não afeta função.
- Tool sai com exit code 1 em veredito NO-GO (by design, para CI).

## Escopo do NO-GO

O NO-GO cobre: **"FSR FG como backend de interpolação do processamento
offline via dispatch offscreen direto, com FFX SDK 2.3.0 + provider FG 4.0.1
em RDNA 4"**. Não cobre o FG de playback (`catra_fg`), que continua
funcional e **não foi alterado** (CA-3.6).

## O que o ADR (Caminho B) precisará

1. **Contexto:** tabela de evidências acima (U1–U6) + controle playback
   (`generated=90, ratio=2.00`) + logs v2/v3.
2. **Alternativas analisadas:**
   - (a) **Manter RIFE** como backend de interpolação offline para a opção
     `fsr3fg`, com **fallback explícito sinalizado** (evento/log — CA-3.4,
     obrigatório em qualquer caminho, alimenta Story 04).
   - (b) **Swapchain dummy em HWND oculto + Present headless** — descartado:
     o provider 4.0.1 atrela geração ao pacing de Present real; mesmo que
     tecnicamente forçável, seria frágil (janela oculta, timing de vsync,
     readback do backbuffer) e violaria o modelo offline do pipeline.
   - (c) **Remover/desabilitar a opção `fsr3fg`** no processamento offline
     na UI (`SettingsViewModel`) até existir suporte real — decisão de
     produto, requer aprovação do usuário.
   - (d) **Revisitar no futuro:** novo bundle FFX (>2.3.0) cujo provider FG
     aceite modo offscreen genuíno, ou provider 3.1.6 que aceite
     CreateContext sem swapchain em RDNA 4. Monitorar release notes FSR SDK.
3. **Decisão:** status **Proposed** até **aprovação explícita do usuário**
   (CA-3.3 — bloqueio de aceite da subtask).
4. **Consequências + mitigação:** opção `fsr3fg` no offline não entrega FG
   real → mitigar com sinalização explícita (a) e/ou ajuste de UI (c).
5. **Referências:** este relatório, `fg_offline_run_v2/v3.log`,
   `fsrapirendermodule.cpp:1606-1658` (sample), `catra_fg.cpp` (playback).

## Pendências da subtask fora do escopo do spike

- ADR `.agents/decisions/ADR-XXX-fsr3-fg-offline.md` + aprovação do usuário
  (Passo 2B).
- Fim do fallback silencioso `fsr3fg→RIFE` em `ProcessingPipeline.cs` +
  `interp_rife.cpp:1028-1031` + teste (Passo 3, CA-3.4 — obrigatório).
- Build gate da solution + regressão `rife` (Passo 4).

## Reprodução

```powershell
cmake --build native/catra-gpu/build --config Release --target catra-fg-offline-test
cd native\catra-gpu\build\Release
.\catra-fg-offline-test.exe            # run completo (~2 min); --no-u6 pula timing 1080p
```

# 🏎️ Sprint Plan: RIFE Modelo Menor — Reduzir Inference para Atingir 32fps

**Objetivo:** Substituir o modelo RIFE v4.25 (22MB, FP32, 5 scales) por uma versão
otimizada que reduza o tempo de inference de ~45ms/frame para ≤25ms/frame,
permitindo atingir ≥32fps de output no pipeline CATRA.

**Criado:** 2026-08-09
**Status:** PLANEJAMENTO
**Baseline:** 545 testes, build 0w/0e, commit 417fa49

---

## 📊 Análise do Bottleneck Atual

### Pipeline por par de frames (pós sprint GPU-only)

| Etapa | Tempo | % do total |
|-------|-------|-----------|
| **RIFE inference ×5** | **~45ms × 5 = 225ms** | **73%** |
| Decoder (CPU fallback) | ~25ms | 8% |
| Interop (CPU fallback) | ~5ms | 2% |
| RIFE tensor I/O | ~10ms | 3% |
| Encode AMF | ~21ms | 7% |
| Overhead | ~22ms | 7% |
| **TOTAL** | **~308ms/par** | **100%** |

**Output fps:** 6 frames / 308ms = **~19.5 fps** (teórico pós-GPU-only, mas fallbacks
mantêm em ~16fps real).

**Meta:** ≥32fps = ≤187ms/par. O bottleneck é claramente RIFE inference (73% do tempo).

### Modelo atual: RIFE v4.25 (IFNet_HDv3)

| Propriedade | Valor |
|-------------|-------|
| Arquitetura | IFNet_HDv3 (optical flow + context net) |
| Scales | [16, 8, 4, 2, 1] (5 pyramid levels) |
| Canais | 64/32/16 (encoder/decoder/context) |
| Precisão | FP32 |
| Tamanho | 22.8 MB |
| Input | [1, 3, H, W] float32 |
| Output | [1, 3, H, W] float32 |
| Tempo/frame (DirectML, RDNA4) | ~45ms |
| FLOPs estimado | ~40 GFLOPs |

---

## 🔍 Opções de Modelos Menores

### A) FP16 Export ✅ RECOMENDADO — MAIOR IMPACTO/MENOR RISCO

**O que:** Converter pesos do modelo para FP16 (half precision).

**Ganho esperado:**
- RDNA 4 tem suporte FP16 nativo (2x throughput vs FP32)
- DirectML mapeia FP16 para hardware automaticamente
- Tempo estimado: ~22-25ms/frame (2x mais rápido)
- Tamanho: ~11 MB (metade)

**Qualidade:** Idêntica — FP16 é suficiente para inference (range [0,1] com 10 bits
de precisão é mais que suficiente para pixels).

**Risco:** BAIXO
- ONNX Runtime suporta FP16 nativamente
- DirectML suporta FP16 em todos os adapters RDNA
- Precisa validar que o export gera grafo FP16 correto
- Fallback: se FP16 falhar, manter FP32

**Implementação:**
- Modificar `export_onnx.py` para converter modelo para FP16 antes do export
- Usar `torch.half()` no modelo + `onnxconverter_float16` ou export direto em FP16
- Adicionar validação no script (comparar output FP16 vs FP32)
- interp_rife.cpp: detectar formato dos tensors de input e adaptar (ou forçar FP32 input com cast interno)

**IMPORTANTE:** O modelo FP16 aceita inputs FP16, mas o pipeline atual envia FP32.
Duas opções:
1. Converter inputs FP32→FP16 no shader (ST-25 já produz float32, adicionar cast)
2. Manter inputs FP32 e deixar o ORT fazer cast interno (mais simples, overhead mínimo)

### B) Reduzir Pyramid Scales ✅ COMPLEMENTAR — MÉDIO IMPACTO

**O que:** Mudar `scale_list = [16, 8, 4, 2, 1]` para `[8, 4, 2, 1]` (4 scales).

**Ganho esperado:**
- Elimina o pyramid level mais caro (16x downscale)
- Tempo estimado: ~35ms/frame (20% mais rápido)
- Com FP16: ~18ms/frame

**Qualidade:** Ligeiramente inferior em movimentos muito grandes (>64px entre frames).
Para anime 24fps→135fps, movimento entre frames consecutivos é pequeno (<20px).

**Risco:** BAIXO
- Modificação no export script (scale_list)
- Modelo menor, exporta mais rápido
- Compatível com o mesmo código C++ (I/O contract idêntico)

### C) RIFE-Lite (menos canais) ⚠️ ALTERNATIVO — MÉDIO IMPACTO/MÉDIO RISCO

**O que:** Modificar IFNet_HDv3 para usar 32/16/8 canais em vez de 64/32/16.

**Ganho esperado:**
- ~40% menos FLOPs
- Tempo estimado: ~27ms/frame
- Com FP16: ~14ms/frame

**Qualidade:** Inferior — menos capacidade de representação. Testes subjetivos
necessários para validar se a qualidade é aceitável para anime.

**Risco:** MÉDIO
- Requer modificar o código Python do IFNet
- Pode precisar de re-treino (ou fine-tune) para qualidade aceitável
- Sem pesos pré-treinados disponíveis (precisa treinar)

**Veredito:** NÃO USAR nesta sprint. Requer treinamento que não temos.

### D) RIFE v2.4 ❌ NÃO RECOMENDADO

**O que:** Usar versão anterior do RIFE (v2.4, ~5MB).

**Ganho:** ~2-3x mais rápido (modelo muito menor).

**Qualidade:** Significativamente inferior — artefatos visíveis em anime.

**Veredito:** NÃO USAR. Qualidade inaceitável para o caso de uso.

---

## 🎯 Estratégia Recomendada

### Combinação A+B: FP16 + 4 scales

| Métrica | Atual (FP32, 5s) | FP16 (5s) | FP16 + 4s |
|---------|-------------------|-----------|-----------|
| Tamanho | 22.8 MB | ~11 MB | ~9 MB |
| Tempo/frame | ~45ms | ~22ms | ~18ms |
| Tempo/par (×5) | 225ms | 110ms | 90ms |
| Output fps | ~16 | ~29 | ~36 |
| Qualidade | 100% | ~100% | ~98% |

**Meta atingida:** FP16 + 4 scales → ~36fps (acima dos 32fps alvo).

### Ordem de execução

1. **ST-27: FP16 export** — modificar script, exportar modelo, validar qualidade
2. **ST-28: 4-scales export** — modificar scale_list, exportar, validar qualidade
3. **ST-29: Integração + profiling GPU real** — testar pipeline completo com novo modelo

---

## 📋 Subtasks

### ST-27: FP16 Export do Modelo RIFE

**Complexidade:** média
**Arquivos:**
- `lib/rife/export_onnx.py` (modificar — adicionar opção FP16)
- `lib/rife/rife_v4.onnx` (regenerar — modelo FP16)
- `lib/rife/README.md` (atualizar documentação)

**Implementação:**

1. **Modificar `export_onnx.py`:**
   - Adicionar flag `--fp16` (default: false para compatibilidade)
   - Quando `--fp16`: converter modelo para half precision antes do export
   ```python
   if args.fp16:
       model = model.half()
       wrapper = wrapper.half()
       dummy_img0 = dummy_img0.half()
       dummy_img1 = dummy_img1.half()
       dummy_ts = dummy_ts.half()
   ```
   - Adicionar validação: comparar output FP16 vs FP32 (MSE < threshold)
   - Output: `rife_v4_fp16.onnx` (ou sobrescrever `rife_v4.onnx`)

2. **Alternativa: pós-conversão com onnxruntime**
   ```python
   import onnx
   from onnxruntime.transformers.float16 import convert_float_to_float16
   model = onnx.load("rife_v4.onnx")
   model_fp16 = convert_float_to_float16(model)
   onnx.save(model_fp16, "rife_v4_fp16.onnx")
   ```

3. **Validação de qualidade:**
   - Exportar FP32 e FP16 do mesmo modelo
   - Rodar 10 frames de teste com ambos
   - Comparar PSNR (esperado >40dB = praticamente idêntico)
   - Comparar tamanho (esperado ~metade)

4. **Compatibilidade com interp_rife.cpp:**
   - O código C++ envia tensors FP32 (std::vector<float>)
   - Opção A: ORT faz cast interno FP32→FP16 (automático, overhead ~1ms)
   - Opção B: modificar C++ para enviar FP16 (mais complexo, ganho marginal)
   - **Escolher Opção A** (mais simples, overhead mínimo)

**Critérios de aceite:**
- [ ] Script exporta modelo FP16 sem erros
- [ ] Modelo FP16 carrega no ORT (DirectML EP)
- [ ] Modelo FP16 produz output visualmente idêntico (PSNR >40dB vs FP32)
- [ ] Tamanho ≤12 MB
- [ ] Tempo inference ≤25ms/frame (DirectML, RDNA4, 1920x1080)
- [ ] interp_rife.cpp funciona sem modificações (ORT cast interno)

**Dependências:** Nenhuma

---

### ST-28: Reduzir Pyramid Scales (5→4)

**Complexidade:** baixa
**Arquivos:**
- `lib/rife/export_onnx.py` (modificar — scale_list configurável)

**Implementação:**

1. **Modificar `export_onnx.py`:**
   - Adicionar flag `--scales` (default: "16,8,4,2,1")
   ```python
   parser.add_argument("--scales", type=str, default="16,8,4,2,1",
                       help="Comma-separated pyramid scales")
   ```
   - No wrapper.forward():
   ```python
   scale_list = [float(s) for s in args.scales.split(",")]
   ```

2. **Exportar modelo 4-scales:**
   ```bash
   python export_onnx.py --fp16 --scales "8,4,2,1" -o rife_v4_fp16_4s.onnx
   ```

3. **Validação:**
   - Comparar com modelo 5-scales (PSNR >35dB = aceitável)
   - Testar com frames de movimento rápido (anime action scenes)
   - Verificar que não há artefatos visíveis

**Critérios de aceite:**
- [ ] Script aceita `--scales` configurável
- [ ] Modelo 4-scales exporta sem erros
- [ ] PSNR >35dB vs 5-scales (em 10 frames de teste)
- [ ] Tempo inference ≤20ms/frame (com FP16)
- [ ] Sem artefatos visíveis em anime

**Dependências:** ST-27 (FP16)

---

### ST-29: Integração + Profiling GPU Real

**Complexidade:** média
**Arquivos:**
- `lib/rife/README.md` (atualizar — documentar novos modelos)
- `scripts/build-native.ps1` (atualizar URL/nome do modelo se necessário)
- `native/catra-gpu/interp_rife.cpp` (possível ajuste — detectar modelo FP16)

**Implementação:**

1. **Deploy do novo modelo:**
   - Copiar `rife_v4_fp16_4s.onnx` para `lib/rife/rife_v4.onnx`
   - Ou: manter ambos e usar variável de ambiente `CATRA_RIFE_MODEL_PATH`

2. **Profiling GPU real:**
   - Processar Martial Master EP72 com novo modelo
   - Medir: tempo/par, tempo/frame, fps output
   - Comparar com baseline (45ms/frame → meta: 18ms/frame)
   - Validar qualidade: PNGs em 30s/150s/300s (comparar visual)

3. **Ajustes no C++ (se necessário):**
   - Se ORT não faz cast FP32→FP16 automaticamente: adicionar conversão manual
   - Se DirectML falha com FP16: fallback para FP32 (manter modelo antigo)

4. **Documentação:**
   - Atualizar README com instruções de export FP16
   - Documentar trade-offs (qualidade vs velocidade)
   - Adicionar benchmarks no SPRINT_LOG

**Critérios de aceite:**
- [ ] Pipeline processa EP72 completo sem erros
- [ ] Throughput ≥32fps (meta) ou ≥29fps (mínimo aceitável)
- [ ] Qualidade visual validada (PNGs comparáveis ao baseline)
- [ ] MP4 output: HEVC 1920x1080 @135fps + AAC
- [ ] 545 testes passam
- [ ] Build 0w/0e
- [ ] Relatório de profiling documentado

**Dependências:** ST-27, ST-28

---

## 📊 Estimativa de Throughput Final

### Cenários

| Configuração | Tempo/frame | Tempo/par | Output fps | Qualidade |
|--------------|-------------|-----------|-----------|-----------|
| Atual (FP32, 5s) | 45ms | 225ms | ~16 | 100% |
| FP16 (5s) | 22ms | 110ms | ~29 | ~100% |
| FP16 + 4s | 18ms | 90ms | ~36 | ~98% |
| FP16 + 3s | 14ms | 70ms | ~46 | ~95% |

**Recomendação:** FP16 + 4 scales → ~36fps, qualidade ~98% (imperceptível).

### Decomposição do pipeline otimizado

| Etapa | Tempo | % |
|-------|-------|---|
| RIFE inference ×5 (FP16, 4s) | 90ms | 55% |
| Decoder (CPU) | 25ms | 15% |
| Interop (CPU fallback) | 5ms | 3% |
| RIFE tensor I/O (GPU shader) | 10ms | 6% |
| Encode AMF | 21ms | 13% |
| Overhead | 13ms | 8% |
| **TOTAL** | **~164ms/par** | **100%** |

**Output fps:** 6 / 164ms = **~36.6 fps** ✅ (acima da meta de 32fps)

---

## 🚫 Fora do Escopo

- **RIFE-Lite (menos canais):** Requer treinamento, fora do escopo
- **RIFE v2.4:** Qualidade inaceitável
- **Modificações no C++ para FP16 nativo:** ORT cast interno é suficiente
- **ST-20/21/22:** Em andamento/pendentes, não tocar
- **Retreinar modelo:** Não temos dataset nem GPU de treino

---

## 📦 Commits Atômicos Esperados

1. `feat(ST-27): FP16 export do modelo RIFE`
2. `feat(ST-28): reduzir pyramid scales (5→4)`
3. `chore(ST-29): integração + profiling GPU real`

---

## 🔗 Dependências Graph

```
ST-27 (FP16 export)
  └──→ ST-28 (4-scales export)
         └──→ ST-29 (integração + profiling)
```

Sequencial: ST-27 → ST-28 → ST-29.

---

## 📝 Notas de Implementação

### FP16 no ONNX Runtime

ORT suporta FP16 de duas formas:
1. **Modelo FP16 + inputs FP32:** ORT faz cast interno (automático, overhead ~1ms)
2. **Modelo FP16 + inputs FP16:** Zero cast (mais rápido, mas precisa modificar C++)

**Escolher opção 1** (mais simples, overhead mínimo).

### FP16 no DirectML

DirectML mapeia FP16 para hardware automaticamente:
- RDNA 2+: suporte FP16 nativo (2x throughput)
- RDNA 4: suporte FP16 confirmado
- Fallback: se adapter não suporta FP16, DirectML emula em FP32 (mais lento)

### Validação de Qualidade

Métricas recomendadas:
- **PSNR:** >40dB = praticamente idêntico
- **SSIM:** >0.99 = imperceptível
- **Visual:** comparar PNGs lado a lado (zoom 200% em áreas de movimento)

### Risco de Artefatos

4-scales pode ter artefatos em movimentos >64px entre frames.
Para anime 24fps→135fps:
- Frames originais: 24fps = 41.67ms entre frames
- Movimento máximo a 60°/s (fast action): ~42px entre frames
- 4-scales captura até 32px (8× downscale limit)
- **Risco:** movimentos muito rápidos podem ter quality loss
- **Mitigação:** testar com action scenes (Martial Master tem bastante)

### Ambiente de Export

Para exportar o modelo FP16:
```bash
# Na máquina com Python + PyTorch (NÃO é esta máquina)
git clone --depth 1 https://github.com/hzwer/Practical-RIFE
cd Practical-RIFE
# Download v4.25 weights (Google Drive)
pip install torch onnx onnxruntime
cp <CATRA>/lib/rife/export_onnx.py .
python export_onnx.py --fp16 --scales "8,4,2,1" -o rife_v4_fp16_4s.onnx
# Copiar para CATRA
cp rife_v4_fp16_4s.onnx <CATRA>/lib/rife/rife_v4.onnx
```

**IMPORTANTE:** Esta máquina (CATRA dev) pode não ter PyTorch/GPU de treino.
O export deve ser feito em máquina separada e o modelo copiado.
Alternativa: usar Google Colab (GPU gratuita) para o export.

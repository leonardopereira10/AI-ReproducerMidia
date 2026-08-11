# Problemas do Fluxo FSR + AMF no Pipeline de Vídeo

## 📋 Visão Geral

Este documento descreve os problemas enfrentados ao integrar o upscale FSR1 (EASU) com o encoder AMF H.265 em um pipeline de processamento de vídeo usando D3D11/D3D12 em uma GPU AMD Radeon RX 9070 XT.

### Hardware e Software
- **GPU**: AMD Radeon RX 9070 XT (RDNA 4)
- **Driver**: AMD Adrenalin Edition
- **APIs**: D3D11 (decode/interp), D3D12 (upscale/encode), interop entre ambas
- **SDKs**: 
  - AMF (AMD Media Framework) para encode H.265
  - FidelityFX SDK NÃO disponível (FSR4 desabilitado, usando FSR1 EASU como fallback)
- **SO**: Windows 11

### Problema Principal
O upscale FSR1 produz texturas D3D12, mas o encoder AMF falha ao consumi-las via `CreateSurfaceFromDX12Native`, retornando `AMF_FAIL` (res=1).

---

## 🏗️ Arquitetura do Pipeline

```
┌─────────────┐
│  FFmpeg     │ Decode NV12 (D3D11)
│  (D3D11VA)  │
└──────┬──────┘
       │ ID3D11Texture2D* (NV12)
       ▼
┌─────────────┐
│  Interop    │ NV12 → BGRA (D3D11)
│  (ST-23)    │
└──────┬──────┘
       │ ID3D11Texture2D* (BGRA)
       ▼
┌─────────────┐
│  RIFE       │ Interpolação (D3D11 → D3D11)
│  (ONNX+DML) │
└──────┬──────┘
       │ ID3D11Texture2D* (BGRA)
       ▼
┌─────────────┐
│  FSR1       │ Upscale (D3D11 → D3D12) ← PROBLEMA AQUI
│  (EASU)     │
└──────┬──────┘
       │ ID3D12Resource* (BGRA)
       ▼
┌─────────────┐
│  AMF        │ Encode H.265 (D3D12)
│  Encoder    │ CreateSurfaceFromDX12Native() FALHA
└──────┬──────┘
       │
       ▼
   Arquivo .mp4
```

---

## 🔍 Código Relevante

### 1. Criação da Textura de Output do FSR1

**Arquivo**: `native/catra-gpu/upscale_fsr1.cpp`

```cpp
// --- Output UAV texture (dstW x dstH, BGRA) ---------------------------
D3D12_RESOURCE_DESC outDesc = {};
outDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
outDesc.Width = static_cast<UINT>(m_dstW);
outDesc.Height = static_cast<UINT>(m_dstH);
outDesc.DepthOrArraySize = 1;
outDesc.MipLevels = 1;
outDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; // BGRA for AMF encoder
outDesc.SampleDesc.Count = 1;
outDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
outDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

D3D12_HEAP_PROPERTIES defaultHeap = {};
defaultHeap.Type = D3D12_HEAP_TYPE_DEFAULT;

ComPtr<ID3D12Resource> dstRes;
HRESULT hr = d.device->CreateCommittedResource(
    &defaultHeap, D3D12_HEAP_FLAG_SHARED, &outDesc,
    D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,
    IID_PPV_ARGS(dstRes.GetAddressOf()));
```

**Observações**:
- Formato: `DXGI_FORMAT_B8G8R8A8_UNORM` (BGRA, compatível com AMF)
- Heap: `D3D12_HEAP_FLAG_SHARED` (necessário para cross-API)
- Flags: Apenas `ALLOW_UNORDERED_ACCESS` (testado com e sem `ALLOW_SIMULTANEOUS_ACCESS`)
- Estado inicial: `D3D12_RESOURCE_STATE_UNORDERED_ACCESS`

### 2. Resource Barrier para COMMON State

**Arquivo**: `native/catra-gpu/upscale_fsr1.cpp`

```cpp
// --- Transition texture to COMMON state for AMF consumption ------------
D3D12_RESOURCE_BARRIER barrier = {};
barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
barrier.Transition.pResource = dstRes.Get();
barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;

hr = d.allocator->Reset();
if (SUCCEEDED(hr))
{
    hr = d.cmdList->Reset(d.allocator.Get(), nullptr);
}
if (SUCCEEDED(hr))
{
    d.cmdList->ResourceBarrier(1, &barrier);
    d.cmdList->Close();
    ID3D12CommandList* lists2[] = {d.cmdList.Get()};
    d.queue->ExecuteCommandLists(1, lists2);
    
    // Wait for transition to complete
    ++d.fenceValue;
    hr = d.queue->Signal(d.fence.Get(), d.fenceValue);
    if (SUCCEEDED(hr) && d.fence->GetCompletedValue() < d.fenceValue)
    {
        hr = d.fence->SetEventOnCompletion(d.fenceValue, d.fenceEvent);
        if (SUCCEEDED(hr))
        {
            WaitForSingleObject(d.fenceEvent, INFINITE);
        }
    }
}
```

**Observações**:
- Transição de `UNORDERED_ACCESS` para `COMMON` antes de passar para o AMF
- Fence wait para garantir que a transição foi completada na GPU

### 3. Encoder AMF Consumindo a Textura

**Arquivo**: `native/catra-gpu/encode_amf.cpp`

```cpp
// --- Wrap the DX12 texture zero-copy as an AMF surface -----------------
// Debug: log texture info before CreateSurfaceFromDX12Native
ID3D12Device* texDevice = nullptr;
if (SUCCEEDED(texture->GetDevice(IID_PPV_ARGS(&texDevice))))
{
    D3D12_RESOURCE_DESC desc = texture->GetDesc();
    BackendLog(CATRA_LOG_INFO, 
        "encode_amf: CreateSurfaceFromDX12Native texture=%p, device=%p, "
        "format=%d, width=%llu, height=%u, flags=%u",
        texture, texDevice, desc.Format, desc.Width, desc.Height, desc.Flags);
    texDevice->Release();
}

AMFSurfacePtr surface;
AMF_RESULT res = d.context2->CreateSurfaceFromDX12Native(texture, &surface, nullptr);
if (res != AMF_OK || surface == nullptr)
{
    BackendLog(CATRA_LOG_ERROR,
        "encode_amf: CreateSurfaceFromDX12Native failed (res=%d)",
        static_cast<int>(res));
    return CATRA_ERR_DEVICE;
}
```

**Log de saída**:
```
encode_amf: CreateSurfaceFromDX12Native texture=0000023B29D7C910, 
    device=0000023B1946B110, format=87, width=2560, height=1440, flags=4
encode_amf: CreateSurfaceFromDX12Native failed (res=1)
```

**Análise do log**:
- `format=87` → `DXGI_FORMAT_B8G8R8A8_UNORM` ✅ (correto)
- `width=2560, height=1440` ✅ (resolução correta)
- `flags=4` → `D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS` ✅
- `device=0000023B1946B110` → Mesmo device usado pelo AMF ✅
- `res=1` → `AMF_FAIL` ❌ (falha genérica)

### 4. Inicialização do AMF Context

**Arquivo**: `native/catra-gpu/encode_amf.cpp`

```cpp
// --- 3. Context (init D3D12 on the bridge's shared device) -------------
res = d.factory->CreateContext(&d.context);
if (res != AMF_OK || d.context == nullptr)
{
    BackendLog(CATRA_LOG_ERROR, "encode_amf: CreateContext failed (res=%d)",
               static_cast<int>(res));
    return CATRA_ERR_DEVICE;
}

// The DX12 entry points (InitDX12 / CreateSurfaceFromDX12Native) are declared
// on AMFContext2, not the base AMFContext returned by CreateContext — QI for it.
d.context2 = AMFContext2Ptr(d.context);
if (d.context2 == nullptr)
{
    BackendLog(CATRA_LOG_ERROR,
               "encode_amf: AMFContext2 QI failed — AMF runtime too old for DX12");
    return CATRA_ERR_DEVICE;
}

res = d.context2->InitDX12(device12);
if (res != AMF_OK)
{
    BackendLog(CATRA_LOG_ERROR,
               "encode_amf: AMFContext2::InitDX12 failed (res=%d) — needs an AMD "
               "DX12 device with encode support", static_cast<int>(res));
    return CATRA_ERR_DEVICE;
}

// --- 6. Init (input format BGRA) ---------------------------------------
res = d.encoder->Init(AMF_SURFACE_BGRA, width, height);
if (res != AMF_OK)
{
    BackendLog(CATRA_LOG_ERROR,
               "encode_amf: encoder Init(%dx%d BGRA) failed (res=%d)", 
               width, height, static_cast<int>(res));
    return CATRA_ERR_DEVICE;
}
```

**Observações**:
- AMF inicializado com `AMF_SURFACE_BGRA` (compatível com a textura)
- `InitDX12` chamado com sucesso
- `CreateSurfaceFromDX12Native` é o método correto para texturas D3D12

### 5. Device D3D12 Compartilhado

**Arquivo**: `native/catra-gpu/d3d_interop.cpp`

```cpp
HRESULT CreateD3D12Device(ID3D11Device* d3d11Device, ID3D12Device** out)
{
    if (out == nullptr) return E_INVALIDARG;
    *out = nullptr;
    if (d3d11Device == nullptr) return E_INVALIDARG;

    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_d3d12Device)
        {
            // Interop is up: hand out the shared bridge device so every
            // backend dispatches on the same D3D12 device (one adapter, one
            // LUID — the precondition for shared-handle interop).
            *out = g_d3d12Device.Get();
            g_d3d12Device->AddRef();
            return S_OK;
        }
    }

    // interop_init has not run (standalone use / catra_init soft-failed):
    // create a private device on the same adapter.
    return CreateD3D12DeviceOnAdapter(d3d11Device, out);
}
```

**Observações**:
- Todos os backends (RIFE, FSR1, AMF) usam o mesmo device D3D12 (`g_d3d12Device`)
- Device criado a partir do adapter do device D3D11
- Garante que todos estão no mesmo adapter/LUID

---

## 🧪 Tentativas Realizadas

### 1. Formato da Textura
- ✅ `DXGI_FORMAT_B8G8R8A8_UNORM` (BGRA) — AMF espera BGRA
- ❌ `DXGI_FORMAT_R8G8B8A8_UNORM` (RGBA) — AMF não aceita

### 2. Heap Flags
- ✅ `D3D12_HEAP_FLAG_SHARED` — Necessário para cross-API
- ❌ `D3D12_HEAP_FLAG_NONE` — AMF não aceita texturas não-compartilhadas

### 3. Resource Flags
- ✅ `D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS` — Necessário para compute shader
- ❌ `D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS` — Testado, não resolveu
- ❌ Combinação de ambas — Testado, não resolveu

### 4. Resource State
- ✅ `D3D12_RESOURCE_STATE_COMMON` — Estado genérico após barrier
- ❌ `D3D12_RESOURCE_STATE_UNORDERED_ACCESS` — Estado após compute dispatch
- ✅ Fence wait após barrier — Garante que a transição foi completada

### 5. Device
- ✅ Mesmo device D3D12 para FSR1 e AMF — Verificado via logs
- ✅ Device criado a partir do mesmo adapter — Garantido por `CreateD3D12Device`

### 6. Formato de Entrada do AMF
- ✅ `AMF_SURFACE_BGRA` — Compatível com `DXGI_FORMAT_B8G8R8A8_UNORM`
- ❌ `AMF_SURFACE_NV12` — Testado anteriormente, não é o problema atual

---

## 💡 Hipóteses e Próximas Abordagens

### Hipótese 1: AMF não aceita texturas D3D12 criadas pelo usuário
**Sintoma**: `CreateSurfaceFromDX12Native` falha com `AMF_FAIL` genérico.

**Possível causa**: O AMF pode ter requisitos específicos não documentados para texturas D3D12:
- Necessidade de keyed mutex (não testado)
- Necessidade de heap específico (não testado)
- Necessidade de texture array (improvável)

**Abordagem**: Investigar documentação do AMF SDK ou exemplos de uso de `CreateSurfaceFromDX12Native`.

### Hipótese 2: Upscale FSR1 deve usar D3D11 em vez de D3D12
**Sintoma**: RIFE produz texturas D3D11 e funciona perfeitamente com AMF.

**Possível causa**: O AMF pode ter melhor suporte para texturas D3D11 via interop do que texturas D3D12 nativas.

**Abordagem**: Reescrever o upscale FSR1 para usar D3D11:
- Compute shader em D3D11 (mais simples que D3D12)
- Textura de output em D3D11 (compatível com AMF via interop)
- Interop D3D11→D3D12 já funciona (usado pelo RIFE)

**Vantagens**:
- Mais confiável (AMF já lida bem com D3D11)
- Compute shader FSR1 em D3D11 é mais simples
- Evita problemas de cross-API com texturas D3D12

**Desvantagens**:
- Requer reescrever o upscale FSR1 (trabalho significativo)
- Perde potencial performance do D3D12 (mas RX 9070 XT é boa em ambos)

### Hipótese 3: Usar pool de texturas do interop (pool12)
**Sintoma**: O pool12 já funciona com AMF (usado pelo interop D3D11→D3D12).

**Possível causa**: O pool12 pode ter configurações específicas que o AMF aceita.

**Abordagem**: Modificar o upscale FSR1 para alocar texturas do pool12 em vez de criar texturas próprias.

**Vantagens**:
- Reutiliza código existente (pool12)
- Já testado com AMF (funciona)

**Desvantagens**:
- Complexidade: integrar upscale com pool
- Pool é round-robin (pode não ser ideal para upscale)
- Tamanho fixo do pool (pode não caber texturas grandes)

### Hipótese 4: Keyed Mutex
**Sintoma**: O interop usa keyed mutex para sincronização cross-API.

**Possível causa**: O AMF pode exigir keyed mutex em texturas D3D12 compartilhadas.

**Abordagem**: Adicionar keyed mutex na textura de output do FSR1:
- Criar textura com `D3D12_HEAP_FLAG_SHARED_KEYEDMUTEX` (se disponível)
- Ou usar `IDXGIKeyedMutex` via QI na textura
- Fazer Acquire/Release ao redor do `CreateSurfaceFromDX12Native`

**Vantagens**:
- Segue o padrão do interop (keyed mutex)
- Pode resolver problemas de sincronização

**Desvantagens**:
- Complexidade adicional
- Pode não ser o problema real

---

## 📊 Logs de Debug

### Log Completo do Pipeline
```
[catra-gpu] interop_init: D3D12 device + DIRECT queue ready (adapter shared with D3D11)
[catra-gpu] catra_init: D3D11<->DX12 interop ready (shared adapter)
[catra-gpu] catra_upscale_create: FSR 4 unavailable -> downgrade to FSR 1
[catra-gpu] upscale_fsr1: EASU pipeline ready 1920x1080 -> 2560x1440
[catra-gpu] catra_upscale_create: FSR 1 1920x1080 -> 2560x1440 (quality=1)
[catra-gpu] encode_amf: HEVC encoder ready 2560x1440 @ 25.000 fps, 30000 kbps (tier=Main)
[catra-gpu] catra_encode_frame: texture is D3D12 (zero-copy), texture=0000023B29D7C910
[catra-gpu] encode_amf: CreateSurfaceFromDX12Native texture=0000023B29D7C910, 
    device=0000023B1946B110, format=87, width=2560, height=1440, flags=4
[catra-gpu] encode_amf: CreateSurfaceFromDX12Native failed (res=1)
```

### Análise
- Upscale FSR1 criado com sucesso (1920x1080 → 2560x1440)
- Encoder AMF criado com sucesso (2560x1440 @ 25fps, 30000 kbps)
- Textura D3D12 detectada corretamente (zero-copy path)
- `CreateSurfaceFromDX12Native` falha com res=1 (AMF_FAIL)
- Device, formato, resolução e flags parecem corretos

---

## 🎯 Recomendações para Resolução

### Prioridade 1: Reescrever Upscale FSR1 para D3D11
**Racional**: 
- RIFE já produz texturas D3D11 e funciona perfeitamente
- AMF tem melhor suporte para D3D11 (via interop)
- Compute shader FSR1 em D3D11 é mais simples
- Evita problemas de cross-API com texturas D3D12

**Passos**:
1. Modificar `upscale_fsr1.cpp` para usar D3D11 em vez de D3D12
2. Compute shader em D3D11 (HLSL → DXBC)
3. Textura de output em D3D11 (BGRA)
4. Interop D3D11→D3D12 já funciona (reutilizar código do RIFE)

**Tempo estimado**: 4-6 horas

### Prioridade 2: Investigar Keyed Mutex
**Racional**:
- Interop usa keyed mutex para sincronização
- AMF pode exigir keyed mutex em texturas D3D12
- Pode ser mais rápido que reescrever upscale

**Passos**:
1. Adicionar keyed mutex na textura de output do FSR1
2. Testar com `IDXGIKeyedMutex` via QI
3. Fazer Acquire/Release ao redor do `CreateSurfaceFromDX12Native`

**Tempo estimado**: 2-3 horas

### Prioridade 3: Usar Pool de Texturas (pool12)
**Racional**:
- Pool12 já funciona com AMF
- Reutiliza código existente

**Passos**:
1. Modificar upscale FSR1 para alocar do pool12
2. Ajustar tamanho do pool se necessário
3. Testar com texturas grandes (2560x1440)

**Tempo estimado**: 3-4 horas

---

## 📚 Referências

### AMF SDK
- `AMF_RESULT` enum: `AMF_OK = 0`, `AMF_FAIL = 1`
- `AMFContext2::CreateSurfaceFromDX12Native`: Cria superfície AMF a partir de textura D3D12
- `AMF_SURFACE_BGRA`: Formato de superfície BGRA (compatível com `DXGI_FORMAT_B8G8R8A8_UNORM`)

### D3D12
- `D3D12_HEAP_FLAG_SHARED`: Necessário para cross-API sharing
- `D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS`: Necessário para compute shader UAV
- `D3D12_RESOURCE_STATE_COMMON`: Estado genérico após barrier

### FSR1 EASU
- Compute shader em HLSL (DXBC/DXIL)
- Input: SRV (texture2D)
- Output: UAV (RWTexture2D)
- Formato: BGRA (compatível com AMF)

---

## 🔗 Links Úteis

- [AMD AMF SDK](https://github.com/GPUOpen-LibrariesAndSDKs/AMF)
- [FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) (não disponível no projeto)
- [D3D12 Documentation](https://docs.microsoft.com/en-us/windows/win32/direct3d12/direct3d-12-graphics)
- [AMD RDNA 4 Architecture](https://www.amd.com/en/products/graphics/radeon-rx-9000-series)

---

## 📝 Notas Finais

- O problema é específico para texturas D3D12 criadas pelo upscale FSR1
- Texturas D3D11 (usadas pelo RIFE) funcionam perfeitamente com AMF
- A GPU RX 9070 XT é boa em D3D12 e Vulkan, mas o AMF parece ter melhor suporte para D3D11
- A solução mais confiável é reescrever o upscale FSR1 para usar D3D11
- Logs detalhados confirmam que device, formato, resolução e flags estão corretos
- O erro `AMF_FAIL` (res=1) é genérico e não fornece detalhes sobre a causa raiz

---

**Data**: 2026-08-10  
**Autor**: Claude (assistente de IA)  
**Status**: Bug aberto, aguardando resolução  
**Impacto**: Perfil DLNA bloqueado (precisa upscale), perfil Local funcional (sem upscale)

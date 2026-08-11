# Prompt: Diagnosticar e corrigir saída preta do FSR1 (EASU compute shader) no RX 9070 XT (AMD RDNA4)

## Contexto

Pipeline de transcodificação offline de vídeo: decoder D3D11VA (FFmpeg, AMD RX 9070 XT, RDNA4) → NV12 → BGRA → interop D3D11↔D3D12 → **FSR1 upscale 1920×1080 → 2560×1440 (EASU compute shader)** → AMF encoder HEVC.

O FSR1 **recebe input válido mas produz output 100% preto**. O encoder AMF aceita a clean texture sem erros, mas os packets HEVC têm ~51 bytes (essencialmente um frame sólido/preto).

## Diagnóstico confirmado por probes

| Probes | Resultado |
|--------|-----------|
| `[readback]` na entrada do FSR1 (CPU round-trip do pool12) | **first4=47,33,26,255** (pixels reais) — 7941/8030 frames com input válido |
| Output do FSR1 via encoder | **packetSize ~51** (preto sólido) — vídeo 100% preto |
| nv12_bgra_convert | E_INVALIDARG (decoder `bind=0x200`, sem `BIND_SHADER_RESOURCE`) — fallback CPU funciona |
| interop GPU-GPU copy | `ReleaseSync hr=0x887A0001` — sempre cai em CPU round-trip |

**Conclusão: o bug está DENTRO do método `Fsr1Upscaler::Process()`** — entre o dispatch do compute shader e o `CopyResource` para a clean texture.

## Método afetado: `Fsr1Upscaler::Process()`

Arquivo: `native/catra-gpu/upscale_fsr1.cpp`

### Fluxo do Process (com estado esperado dos recursos)

```cpp
int Fsr1Upscaler::Process(ID3D11Texture2D* src, ID3D12Resource** outDst)
{
    // 1. ShareTexture: D3D11 BGRA src → DX12 srcRes (pool12 via CPU round-trip)
    //    srcRes tem estado COMMON (pós-barrier do UploadCpuTo12)
    ComPtr<ID3D12Resource> srcRes;
    HRESULT hr = ShareTexture(d.d3d11Device, d.device.Get(), src, srcRes.GetAddressOf());

    // 2. Keyed-mutex acquire (no-op se mutex == null)
    KeyedMutexGuard mutexGuard;
    // ...

    // 3. Criar dstRes (UAV output) — criado toda vez com UNORDERED_ACCESS
    D3D12_RESOURCE_DESC outDesc = {};
    outDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    outDesc.Width = static_cast<UINT>(m_dstW);    // 2560
    outDesc.Height = static_cast<UINT>(m_dstH);   // 1440
    outDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    outDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    ComPtr<ID3D12Resource> dstRes;
    hr = d.device->CreateCommittedResource(
        &defaultHeap, D3D12_HEAP_FLAG_NONE, &outDesc,
        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr,  // ← criado em UAV
        IID_PPV_ARGS(dstRes.GetAddressOf()));

    // 4. Criar SRV para srcRes (slot 0) e UAV para dstRes (slot 1)
    D3D12_CPU_DESCRIPTOR_HANDLE heapCpu = d.srvHeap->GetCPUDescriptorHandleForHeapStart();
    D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    srvDesc.Texture2D.MipLevels = 1;
    d.device->CreateShaderResourceView(srcRes.Get(), &srvDesc, heapCpu);  // ← VOID (sem HRESULT)

    D3D12_CPU_DESCRIPTOR_HANDLE uavCpu = heapCpu;
    uavCpu.ptr += d.cbvSrvUavSize;
    D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc = {};
    uavDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    d.device->CreateUnorderedAccessView(dstRes.Get(), nullptr, &uavDesc, uavCpu);  // ← VOID (sem HRESULT)

    // 5. Record + dispatch
    hr = d.allocator->Reset();
    hr = d.cmdList->Reset(d.allocator.Get(), d.pipelineState.Get());

    d.cmdList->SetComputeRootSignature(d.rootSignature.Get());
    ID3D12DescriptorHeap* heaps[] = {d.srvHeap.Get()};
    d.cmdList->SetDescriptorHeaps(1, heaps);
    d.cmdList->SetComputeRootConstantBufferView(0, d.constBuffer->GetGPUVirtualAddress());
    d.cmdList->SetComputeRootDescriptorTable(1, d.srvHeap->GetGPUDescriptorHandleForHeapStart());

    const UINT groupsX = (static_cast<UINT>(m_dstW) + 15) / 16;  // 160
    const UINT groupsY = (static_cast<UINT>(m_dstH) + 15) / 16;  // 90
    d.cmdList->Dispatch(groupsX, groupsY, 1);
    d.cmdList->Close();

    ID3D12CommandList* lists[] = {d.cmdList.Get()};
    d.queue->ExecuteCommandLists(1, lists);

    // 6. Fence wait + device-removal probe
    ++d.fenceValue;
    hr = d.queue->Signal(d.fence.Get(), d.fenceValue);
    if (SUCCEEDED(hr) && d.fence->GetCompletedValue() < d.fenceValue) {
        hr = d.fence->SetEventOnCompletion(d.fenceValue, d.fenceEvent);
        if (SUCCEEDED(hr)) WaitForSingleObject(d.fenceEvent, INFINITE);
    }
    {
        HRESULT removedHr = d.device->GetDeviceRemovedReason();
        if (FAILED(removedHr)) { /* log + return error */ }
    }

    // 7. Criar cleanRes (sem UAV flag) e CopyResource dstRes → cleanRes
    D3D12_RESOURCE_DESC cleanDesc = {};
    cleanDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    cleanDesc.Width = static_cast<UINT>(m_dstW);
    cleanDesc.Height = static_cast<UINT>(m_dstH);
    cleanDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    cleanDesc.Flags = D3D12_RESOURCE_FLAG_NONE;  // sem UAV → AMF aceita
    ComPtr<ID3D12Resource> cleanRes;
    hr = d.device->CreateCommittedResource(
        &defaultHeap, D3D12_HEAP_FLAG_NONE, &cleanDesc,
        D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
        IID_PPV_ARGS(cleanRes.GetAddressOf()));

    // 8. Barriers + CopyResource
    D3D12_RESOURCE_BARRIER barriers[2] = {};
    barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[0].Transition.pResource = dstRes.Get();
    barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;

    // ... allocator/cmdList reset ...
    d.cmdList->ResourceBarrier(1, &barriers[0]);
    d.cmdList->CopyResource(cleanRes.Get(), dstRes.Get());

    // Barriers revert + clean → COMMON
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    barriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[1].Transition.pResource = cleanRes.Get();
    barriers[1].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    barriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    d.cmdList->ResourceBarrier(2, barriers);
    d.cmdList->Close();

    // Execute + fence wait + device-removal probe (igual ao passo 6)
    // ...

    *outDst = cleanRes.Detach();  // caller owns
    return CATRA_OK;
}
```

### Root signature e constant buffer

```cpp
// Root signature: params[0] = CBV (b0), params[1] = descriptor table (t0 SRV + u0 UAV)
D3D12_ROOT_PARAMETER params[2] = {};
params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
params[0].Descriptor.ShaderRegister = 0;   // b0
params[0].Descriptor.RegisterSpace = 0;
params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
params[1].DescriptorTable.NumDescriptorRanges = 2;
params[1].DescriptorTable.pDescriptorRanges = ranges;
params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

// Constant buffer (UPLOAD heap, 256 bytes)
cbDesc.Width = 256;  // alinhado a D3D12_CONSTANT_BUFFER_DATA_PLACEMENT_ALIGNMENT
// Conteúdo: float constants[4] = {srcW, srcH, dstW, dstH}
// = {1920, 1080, 2560, 1440}
```

### EASU compute shader (HLSL embutido)

```hlsl
Texture2D<float4>   InputTex  : register(t0);
RWTexture2D<float4> OutputTex : register(u0);

cbuffer EasuConst : register(b0)
{
    float2 SrcSize;    // 1920, 1080
    float2 DstSize;    // 2560, 1440
};

[numthreads(16, 16, 1)]
void CSMain(uint3 gid : SV_DispatchThreadID)
{
    if (gid.x >= (uint)DstSize.x || gid.y >= (uint)DstSize.y)
    {
        return;  // ← se DstSize=0, TODOS os threads retornam → output preto
    }

    float2 scale  = SrcSize / DstSize;
    float2 inPos  = ((float2)gid.xy + 0.5) * scale - 0.5;
    // ... Lanczos-2 resample + edge-adaptive clamp ...
    OutputTex[int2(gid.xy)] = float4(color, 1.0);
}
```

## Hipóteses (ordem de prioridade)

### H1 (mais provável): SRV de srcRes inválido (CreateShaderResourceView falha silenciosamente)

`CreateShaderResourceView` é **void** — não retorna HRESULT. Se o srcRes (pool12 via CPU round-trip) tiver algum problema (formato incorreto, estado incompatível, ou recurso inválido), a função falha silenciosamente e o descritor fica zerado/garbage. O dispatch então lê zeros do SRV → todos os pixels do input são 0 → Lanczos-2 produz 0 → output preto.

**Teste**: adicionar debug layer (`ID3D12Debug`) e verificar se há mensagens de validação. Ou fazer readback do srcRes ANTES do dispatch para confirmar que o SRV lê os dados corretos.

### H2: UAV de dstRes inválido (CreateUnorderedAccessView falha silenciosamente)

Mesmo problema de H1 mas no lado do UAV. Se o UAV está zerado, o dispatch escreve no vazio (undefined behavior no AMD). O output fica 0.

**Teste**: idem H1 — debug layer ou readback.

### H3: Constant buffer não está sendo lido corretamente (DstSize = 0 → early-out)

O shader faz `if (gid.x >= (uint)DstSize.x || gid.y >= (uint)DstSize.y) return;`. Se `DstSize` for lido como (0,0), **todos os threads retornam sem escrever nada** → dstRes fica com o conteúdo de inicialização (zeros) → preto.

**Teste**: adicionar `Root Constants` em vez de CB para eliminar a variável. Ou ler back o CB via CPU para confirmar o conteúdo.

### H4: srcRes em estado errado para SRV (espera COMMON, está em outro estado)

O pool12 slot é criado em `COPY_DEST`, transicionado para `COMMON` pelo UploadCpuTo12. Se o slot foi reutilizado e o state tracking (fix H1 anterior) está correto, deveria estar em `COMMON`. Mas se há um bug no state tracking, o slot poderia estar em estado diferente → leitura via SRV retorna garbage/zeros.

**Teste**: logar o `state` do slot antes do Process, ou adicionar uma transition barrier explícita `srcRes → SRV-friendly state` (como `NON_PIXEL_SHADER_RESOURCE`).

### H5: Dispatch não está executando (command list vazio ou falha de reset)

O `allocator->Reset()` ou `cmdList->Reset()` pode estar falhando silenciosamente (HRESULT não checado em todos os pontos). Se o command list não foi resetado corretamente, `Dispatch` não grava nada → output preto.

**Teste**: verificar todos os HRESULTs de Reset() e Close().

### H6: CopyResource dst→clean falha silenciosamente (estado de dstRes errado)

Após o dispatch, dstRes está em `UNORDERED_ACCESS`. O barrier `UAV → COPY_SOURCE` deve funcionar. Mas se o dispatch não completou corretamente (mesmo com fence wait), o recurso pode estar em estado inconsistente → CopyResource copia zeros.

**Teste**: readback do dstRes DIRETAMENTE (antes do CopyResource) para confirmar que o dispatch escreveu pixels.

## Correções já aplicadas (que NÃO resolveram)

- **Pool12 barrier tracking**: state tracking por slot (`state` field). Pre-barrier COMMON→COPY_DEST, post-barrier COPY_DEST→COMMON.
- **interop_shutdown**: clear de `g_pool12`, reset de `g_pool12Desc` e `g_pool12Index`.
- **CB alignment**: `cbDesc.Width = 256` (era 16, violava spec de 256-byte alignment).
- **nv12_bgra_convert**: robust SRV (query desc, dynamic TEXTURE2D/TEXTURE2DARRAY). Revelou `bind=0x200` (sem BIND_SHADER_RESOURCE). Fallback CPU funciona.

## Próximo passo sugerido

1. **Adicionar readback do dstRes** (UAV output) logo após o dispatch + fence wait, ANTES do CopyResource. Isso confirma se o dispatch escreveu pixels ou se dstRes está zerado.
   - Se dstRes é válido (pixels coloridos): bug está no CopyResource dst→clean ou na clean texture.
   - Se dstRes é zero: bug está no dispatch (H1/H2/H3/H4/H5).

2. **Habilitar D3D12 Debug Layer** (`ID3D12Debug`) para capturar erros de validação de CreateShaderResourceView/CreateUnorderedAccessView que são silenciosamente ignorados.

3. **Adicionar log de `GetDeviceRemovedReason()`** após cada `ExecuteCommandLists` para detectar TDR silencioso.

## Arquivos relevantes

- `native/catra-gpu/upscale_fsr1.cpp` — FSR1 pipeline (Process, Create, shader HLSL)
- `native/catra-gpu/d3d_interop.cpp` — interop D3D11↔D3D12 (ShareTexture, UploadCpuTo12, ReadBack11ToCpu, pool12)
- `native/catra-gpu/nv12_to_bgra_shader.cpp` — GPU NV12→BGRA (falha com bind=0x200, fallback CPU funciona)
- `src/CATRA.Services/Processing/FrameDecoder.cs` — C# fallback (ConvertAndUploadNv12)

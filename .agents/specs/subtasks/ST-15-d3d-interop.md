# Subtask 15: D3D11↔DX12 Texture Interop

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Pipeline, Native Bridge)
Depende de ST-12 (native bridge) e ST-05 (D3D11 device do decoder).

## Objetivo
Compartilhar texturas GPU entre D3D11 (FFmpeg decode output) e DX12
(FSR 4 compute, AMF encode) sem copiar para CPU. Shared textures com
keyed mutex para sincronização.

## Escopo
### Arquivos a Criar
- `native/catra-gpu/d3d_interop.cpp` — implementação completa
- `native/catra-gpu/d3d_interop.h` — header (já existe como stub da ST-12)
- `tests/native/test_interop.cpp` — teste nativo

### Arquivos a Modificar
- `native/catra-gpu/catra_gpu.cpp` — usar interop no init
- `native/catra-gpu/CMakeLists.txt` — garantir d3d11.lib + d3d12.lib + dxgi.lib

### Arquivos NÃO tocar
- `native/catra-gpu/interp_rife.cpp` — ST-13
- `native/catra-gpu/upscale_fsr4.cpp` — ST-14
- `src/CATRA.Services/Playback/` — não modificar

## Requisitos Técnicos

### Shared Texture Flow
```
FFmpeg (D3D11)                    Native Bridge (DX12)
     │                                  │
     ▼                                  ▼
ID3D11Texture2D ──shared handle──► ID3D12Resource
  (frame decode)    (NT handle)     (FSR 4 input)
     │                                  │
     │         keyed mutex              │
     │◄───── sync ─────────────────────►│
     │                                  │
ID3D11Texture2D ◄──shared handle── ID3D12Resource
  (render local)                  (FSR 4 output / AMF input)
```

### API Interna (d3d_interop.h)
```cpp
// Cria D3D12 device + command queue compartilhando adapter com D3D11
int interop_init(ID3D11Device* d3d11_device,
                 ID3D12Device** out_d3d12_device,
                 ID3D12CommandQueue** out_cmd_queue);

// D3D11 → DX12: cria shared texture DX12 a partir de textura D3D11
int interop_share_d3d11_to_d3d12(ID3D11Texture2D* src,
                                  ID3D12Resource** out_d3d12_tex,
                                  HANDLE* out_shared_handle);

// DX12 → D3D11: cria shared texture D3D11 a partir de textura DX12
int interop_share_d3d12_to_d3d11(ID3D12Resource* src,
                                  ID3D11Device* d3d11_device,
                                  ID3D11Texture2D** out_d3d11_tex);

// Sincronização via keyed mutex
int interop_acquire(IDXGIKeyedMutex* mutex, uint64_t key, uint32_t timeout_ms);
int interop_release(IDXGIKeyedMutex* mutex, uint64_t key);

// Copia textura D3D11 → shared (para frames do FFmpeg que não são shared)
int interop_copy_d3d11(ID3D11DeviceContext* ctx,
                       ID3D11Texture2D* src,
                       ID3D11Texture2D* dst_shared);

void interop_shutdown();
```

### Detalhes de Implementação

#### interop_init
- Obter `IDXGIDevice` do D3D11 device → `IDXGIAdapter`
- `D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_12_0, ...)`
- Criar `ID3D12CommandQueue` (DIRECT type)
- Criar `ID3D12CommandAllocator` + `ID3D12GraphicsCommandList`
- Criar fence para sincronização CPU-GPU

#### interop_share_d3d11_to_d3d12
- Verificar se textura D3D11 tem `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`
  - FFmpeg D3D11VA: texturas do decoder pool geralmente são shared
  - Se não: criar textura shared D3D11 + `CopyResource` (interop_copy_d3d11)
- `IDXGIResource1::CreateSubresourceSurface` ou `GetSharedHandle`
- `ID3D12Device::OpenSharedHandle` → `ID3D12Resource`
- Preservar formato (DXGI_FORMAT), dimensões, mip levels

#### interop_share_d3d12_to_d3d11
- `ID3D12Device::CreateSharedHandle` → NT handle
- `ID3D11Device::OpenSharedResource1` → `ID3D11Texture2D`
- Textura D3D11 com `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`

#### Sincronização (Keyed Mutex)
- `IDXGIKeyedMutex::AcquireSync(key, timeout)` — espera GPU terminar
- `IDXGIKeyedMutex::ReleaseSync(key)` — sinaliza conclusão
- Keys: alternar 0/1 por frame (evitar deadlock)
- Timeout: 5000ms (fallback: retornar erro)

#### Pool de Texturas
- Não criar/destroy shared textures por frame (caro)
- Pool de N texturas shared (N = frames em trânsito, tipicamente 4-8)
- Round-robin: frame N usa textura N % pool_size
- Resize pool se resolução mudar

### Performance
- Zero-copy: nenhuma cópia CPU envolvida
- Overhead: ~0.1ms por share (NT handle open)
- Pool elimina overhead de criação por frame
- Keyed mutex: ~0.01ms por acquire/release

## Critérios de Sucesso
- [ ] `cmake --build` compila
- [ ] D3D12 device criado no mesmo adapter do D3D11
- [ ] Textura D3D11 → DX12 compartilhada sem cópia CPU
- [ ] Textura DX12 → D3D11 compartilhada sem cópia CPU
- [ ] Keyed mutex sincroniza corretamente (sem race condition)
- [ ] Pool de texturas funciona (sem criação por frame)
- [ ] Teste: 1000 frames compartilhados sem crash ou leak
- [ ] Teste: resolução muda (720p→1080p) sem crash
- [ ] GPU memory estável após 1000 frames (sem crescimento)

## Dependências
- ST-12 (native bridge skeleton)
- ST-05 (D3D11 device do FFmpeg decoder — para teste integrado)

## Notas
- FFmpeg D3D11VA output: `AVFrame.data[0]` = `ID3D11Texture2D*`,
  `AVFrame.data[1]` = subresource index
- Texturas do FFmpeg são do decoder pool — podem não ser shared
  - Se não forem: `CopyResource` para textura shared (uma cópia GPU-GPU, barato)
- NT handles vs legacy handles: NT handles são mais robustos (Windows 8+)
- `IDXGIResource1::CreateSharedHandle` requer `D3D11_RESOURCE_MISC_SHARED_NTHANDLE`
- Alternativa: `D3D11_RESOURCE_MISC_SHARED` (legacy, KMT handle) — menos seguro
- Debug: `D3D12_DEBUG_DEVICE` se `_DEBUG` definido

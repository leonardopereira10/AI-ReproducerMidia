# Subtask 08: DLNA Casting Raw

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-06, Integração DLNA/UPnP, HTTP Server)
Depende de ST-05 (PlaybackEngine para controles).

## Objetivo
Transmitir arquivo de vídeo (original, sem processamento) para Samsung Smart TV
via DLNA/UPnP. Discovery SSDP, HTTP server com Range requests, controle remoto
(play/pause/seek/volume) via AVTransport.

## Escopo
### Arquivos a Criar
- `src/CATRA.Services/Casting/DlnaDiscoveryService.cs` — SSDP M-SEARCH
- `src/CATRA.Services/Casting/DlnaDevice.cs` — model de dispositivo encontrado
- `src/CATRA.Services/Casting/AvTransportClient.cs` — SOAP AVTransport
- `src/CATRA.Services/Casting/RenderingControlClient.cs` — SOAP volume
- `src/CATRA.Services/Casting/MediaHttpServer.cs` — Kestrel embedded, Range requests
- `src/CATRA.Services/Casting/CastingService.cs` — orquestra discovery + serve + control
- `src/CATRA.Core/Interfaces/ICastingService.cs`
- `src/CATRA.Core/Interfaces/IDlnaDiscoveryService.cs`
- `src/CATRA.Core/Interfaces/IMediaHttpServer.cs`
- `src/CATRA.Core/Models/DlnaDeviceInfo.cs` — name, address, port, capabilities
- `src/CATRA.Core/Models/CastingState.cs` — Idle, Connecting, Streaming, Error
- `src/CATRA.UI/Controls/CastButtonControl.xaml` + `.cs` — dropdown de dispositivos
- `tests/CATRA.Services.Tests/Casting/DlnaDiscoveryTests.cs`
- `tests/CATRA.Services.Tests/Casting/MediaHttpServerTests.cs`

### Arquivos a Modificar
- `src/CATRA.UI/ViewModels/PlayerViewModel.cs` — integrar botão Transmitir
- `src/CATRA.App/App.xaml.cs` — registrar casting services + iniciar HTTP server

### Arquivos NÃO tocar
- `src/CATRA.Services/Playback/` — não modificar
- `src/CATRA.Data/` — sem DB

## Requisitos Técnicos

### DlnaDiscoveryService (SSDP)
- UDP multicast M-SEARCH para `239.255.255.250:1900`
- Search target: `urn:schemas-upnp-org:device:MediaRenderer:1`
- Timeout: 3s, retry a cada 10s enquanto dropdown aberto
- Parse response: LOCATION header → GET description XML → extrair friendlyName,
  AVTransport controlURL, RenderingControl controlURL
- Evento `DevicesFound(List<DlnaDeviceInfo>)`
- Filtrar: só dispositivos com AVTransport service

### MediaHttpServer (Kestrel Embedded)
- `GET /media/{token}` — serve arquivo com Range requests
- Token: GUID gerado por sessão de casting (evita path traversal)
- Headers: `Content-Type: video/mp4`, `Accept-Ranges: bytes`,
  `Content-Length`, `Content-Range` (para 206)
- Suporte a `Range: bytes=START-END` e `Range: bytes=START-`
- Bind: `0.0.0.0:{porta}` — porta aleatória disponível
- `GetLocalIp()` — detectar IP da LAN (não localhost)
- URL final: `http://{localIp}:{port}/media/{token}`
- Firewall: tentar `netsh advfirewall firewall add rule` no primeiro run
  - Se falhar (sem admin): log warning + instrução manual

### AvTransportClient (SOAP)
- `SetAVTransportURI(uri, metadata)` — DIDL-Lite metadata XML
- `Play()`, `Pause()`, `Stop()`
- `Seek(unit, target)` — unit: `REL_TIME`, target: `HH:MM:SS`
- `GetPositionInfo()` → posição atual na TV
- `GetTransportInfo()` → estado (PLAYING, PAUSED, STOPPED)
- SOAP: HTTP POST com `Content-Type: text/xml; charset="utf-8"`
- DIDL-Lite metadata:
  ```xml
  <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">
    <item id="0" parentID="-1" restricted="1">
      <dc:title>{episode title}</dc:title>
      <res protocolInfo="http-get:*:video/mp4:DLNA.ORG_OP=01;DLNA.ORG_CI=0">{url}</res>
    </item>
  </DIDL-Lite>
  ```

### RenderingControlClient (SOAP)
- `SetVolume(channel, volume)` — channel: Master, volume: 0-100
- `GetVolume(channel)` → volume atual

### CastingService (Orquestrador)
```csharp
public interface ICastingService
{
    Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync();
    Task StartCastingAsync(DlnaDeviceInfo device, string filePath, string title);
    Task PlayAsync();
    Task PauseAsync();
    Task StopCastingAsync();
    Task SeekAsync(TimeSpan position);
    Task SetVolumeAsync(int volume);
    Task<TimeSpan> GetPositionAsync();
    CastingState State { get; }
    event EventHandler<CastingState> StateChanged;
    event EventHandler<TimeSpan> PositionChanged;
}
```
- `StartCastingAsync`: registrar arquivo no HttpServer → obter URL →
  SetAVTransportURI → Play
- `StopCastingAsync`: Stop → desregistrar arquivo → (não parar HttpServer)
- Polling de posição: timer 1s → GetPositionInfo → PositionChanged
- Error handling: se TV não responde → State = Error + mensagem

### CastButtonControl (UI)
- Botão "📺 Transmitir" no player
- Clique → dropdown com dispositivos encontrados
- "🔍 Procurando..." enquanto discovery roda
- Selecionar dispositivo → inicia casting
- Durante casting: botão vira "📺 Samsung TV" com indicator
- Clique durante casting → dropdown com controles (Play/Pause/Stop/Volume)

### Integração PlayerViewModel
- Botão "Transmitir" habilitado quando player tem arquivo carregado
- Ao iniciar casting: player local pausa (não para)
- Seek no player local → seek na TV (se casting ativo)
- Play/Pause local → play/pause na TV (se casting ativo)
- Status bar: "Transmitindo para {device} [{position}]"

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] `dotnet test` passa
- [ ] Discovery encontra Samsung Smart TV na rede
- [ ] HTTP server serve arquivo com Range requests corretos
- [ ] TV reproduz vídeo via DLNA
- [ ] Play/Pause/Stop remotos funcionam
- [ ] Seek remoto funciona
- [ ] Volume remoto funciona
- [ ] Posição da TV reportada no player local
- [ ] Dropdown lista dispositivos corretamente
- [ ] Firewall exception solicitada no primeiro uso
- [ ] Funciona com arquivos MP4 e AVI

## Dependências
- ST-05 (PlaybackEngine — para pausar local ao transmitir)

## Notas
- Rssdp (NuGet) pode simplificar SSDP discovery — avaliar vs raw UDP
- Samsung TVs são DLNA-certified, devem funcionar sem SDK proprietário
- H.265 em MP4: Samsung TVs modernas suportam; TVs antigas podem não suportar
  - Nesta subtask: transmitir arquivo original (codec do source)
  - Fase 2: transmitir processado H.265 (garantido)
- DLNA.ORG_OP=01: permite seek (operations byte)
- Testar com arquivo real de 1.6GB (SUACLPLNDRS.mp4) para validar Range requests

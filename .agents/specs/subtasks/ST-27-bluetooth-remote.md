# Subtask 27: Bluetooth Controle Remoto — STUB

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RF-11, Fase 4)
Depende de ST-06 (PlayerView) e ST-08 (CastingService).

## Objetivo
Parear com celular via Bluetooth e usar como controle remoto enquanto
o vídeo transmite na TV via DLNA.

## Escopo (resumido — detalhar na Fase 4)
### Arquivos a Criar
- `src/CATRA.Services/RemoteControl/BluetoothService.cs`
- `src/CATRA.Services/RemoteControl/RemoteControlProtocol.cs`
- `src/CATRA.Services/RemoteControl/RemoteControlServer.cs`
- `src/CATRA.Core/Interfaces/IBluetoothService.cs`

## Requisitos Principais
- Windows.Devices.Bluetooth (WinRT) para pareamento
- RFCOMM ou BLE GATT para comunicação
- Protocolo custom (JSON binário):
  - `play`, `pause`, `stop`, `seek(position)`, `volume(0-100)`, `skip_intro`
  - `get_state` → retorna posição, duração, estado
- Server no PC: escuta comandos do celular
- Celular: app companion (Fase 4 — pode ser web app via Bluetooth Web API)
- Latência: < 200ms entre comando e ação
- Multi-device: 1 celular por vez (último pareado)

## Critérios de Aceitação
- [ ] Pareamento Bluetooth funciona
- [ ] Play/Pause do celular controla playback
- [ ] Seek do celular reposiciona vídeo
- [ ] Volume controla áudio
- [ ] Funciona enquanto DLNA transmite na TV

## Dependências
- ST-06 (PlayerView)
- ST-08 (CastingService)

## Notas
- Bluetooth Web API (celular): Chrome/Edge suportam Web Bluetooth
- Alternativa: WebSocket na LAN (mais simples, sem pareamento)
  - Mas spec pede Bluetooth explicitamente
- RFCOMM: `Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService`
- BLE GATT: `Windows.Devices.Bluetooth.GenericAttributeProfile`
- Avaliar: BLE tem throughput baixo (~1Mbps) — suficiente para comandos

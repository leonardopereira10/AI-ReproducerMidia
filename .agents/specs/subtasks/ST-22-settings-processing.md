# Subtask 22: Settings Processamento + Perfis

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Tela 6, AppSettings keys)
Depende de ST-19 (UI pre-processar) e ST-11 (Settings base).

## Objetivo
Habilitar a seção "Processamento" da Settings (disabled desde ST-11).
Controles para janela, métodos de interp/upscale, e perfis local/dlna
(resolução, fps, bitrate). Aplica mudanças em tempo real.

## Escopo
### Arquivos a Modificar
- `src/CATRA.UI/Views/SettingsView.xaml` — habilitar seção Processamento
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs` — properties de processamento

### Arquivos NÃO tocar
- `src/CATRA.Services/` — serviços já leem de AppSettings
- `src/CATRA.Data/` — schema não muda

## Requisitos Técnicos

### Seção Processamento (habilitar)
- Remover `IsEnabled="False"` da seção (colocada na ST-11)
- Controls:

**Geral:**
- Janela de episódios: NumericUpDown (1-20, default 5)
- Método interpolação: ComboBox ["RIFE v4", "FSR 3 FG"] (default RIFE)
- Método upscale: ComboBox ["FSR 4", "FSR 1"] (default FSR 4)

**Perfil Local:**
- Resolução: dois NumericUpDown (width × height, default 1920×1080)
- FPS: NumericUpDown (24-240, default 135)
- Bitrate: NumericUpDown (1000-100000 kbps, default 20000)
- Label estimativa: "~3.3 GB por episódio (22min)"

**Perfil DLNA:**
- Resolução: dois NumericUpDown (default 3840×2160)
- FPS: NumericUpDown (default 55)
- Bitrate: NumericUpDown (default 45000)
- Label estimativa: "~7.4 GB por episódio (22min)"

### SettingsViewModel — Novas Properties
```csharp
[ObservableProperty] private int _windowSize;
[ObservableProperty] private string _interpMethod;
[ObservableProperty] private string _upscaleMethod;
[ObservableProperty] private int _localTargetFps;
[ObservableProperty] private int _localTargetWidth;
[ObservableProperty] private int _localTargetHeight;
[ObservableProperty] private int _localEncodeBitrateKbps;
[ObservableProperty] private int _dlnaTargetFps;
[ObservableProperty] private int _dlnaTargetWidth;
[ObservableProperty] private int _dlnaTargetHeight;
[ObservableProperty] private int _dlnaEncodeBitrateKbps;
```
- Cada setter → `AppSettingsRepository.Set(key, value)`
- Estimativa calculada: `duration_avg × bitrate / 8 / 1024³` GB
  - duration_avg: 22min fixo como referência (label)

### Validação
- FPS: mínimo 24, máximo 240
- Resolução: mínimo 640×360, máximo 7680×4320
- Bitrate: mínimo 1000 kbps, máximo 200000 kbps
- Window size: mínimo 1, máximo 20
- Se FSR 4 selecionado mas não disponível → warning + sugestão FSR 1

### Integração
- Mudança de window_size → `SlidingWindowService` usa na próxima janela
- Mudança de perfil → próximo `StartWindowAsync` usa novos valores
- Não afeta processamento em andamento (só novos jobs)
- Mudança de interp/upscale → próximo job usa novo método

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Seção Processamento visível e habilitada
- [ ] Todos os valores carregam do DB
- [ ] Mudanças persistem imediatamente
- [ ] Estimativa de tamanho atualiza ao mudar bitrate/resolução
- [ ] Validação impede valores absurdos
- [ ] Warning se FSR 4 indisponível
- [ ] Próximos jobs usam valores atualizados

## Dependências
- ST-19 (UI pre-processar — contexto)
- ST-11 (Settings base)

## Notas
- Subtask simples — basicamente habilitar + bindar controles
- NumericUpDown: usar `CommunityToolkit.WinUI.UI.Controls` ou custom
  - WPF não tem NumericUpDown nativo — criar controle simples
- Estimativa é aproximada (bitrate × duração) — suficiente para UX

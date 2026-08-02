# Subtask 10: Theme Windows (Light/Dark)

## Contexto
Spec: `.agents/specs/catra-media-player.md` (RNF: seguir tema do Windows)
Depende de ST-01 (estrutura WPF).

## Objetivo
App segue automaticamente o tema light/dark do Windows. ResourceDictionaries
para ambos os temas. Detecção de mudança em tempo real (sem restart).
Override manual opcional via Settings.

## Escopo
### Arquivos a Criar
- `src/CATRA.UI/Themes/LightTheme.xaml` — cores light
- `src/CATRA.UI/Themes/DarkTheme.xaml` — cores dark
- `src/CATRA.UI/Themes/SharedStyles.xaml` — estilos independentes de tema
- `src/CATRA.Services/ThemeService.cs` — detecção + swap
- `src/CATRA.Core/Interfaces/IThemeService.cs`
- `src/CATRA.Core/Enums/AppTheme.cs` — System, Light, Dark

### Arquivos a Modificar
- `src/CATRA.App/App.xaml` — merge SharedStyles, theme inicial
- `src/CATRA.App/App.xaml.cs` — iniciar ThemeService

### Arquivos NÃO tocar
- `src/CATRA.Data/` — sem DB
- `src/CATRA.Services/Playback/` — sem player

## Requisitos Técnicos

### ThemeService
- Ler registro: `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize`
  - `AppsUseLightTheme` = 0 → dark, 1 → light
- `RegistryWatcher` ou polling (5s) para detectar mudança
- Override: `AppSettings["theme_override"]` = "system" | "light" | "dark"
- Se override != system → ignorar registro
- Swap: remover ResourceDictionary atual, adicionar novo
- Evento `ThemeChanged(AppTheme)` para UI reagir

### ResourceDictionaries
- `LightTheme.xaml`: Background=#FFFFFF, Foreground=#1A1A1A, Accent=#0078D4, CardBg=#F5F5F5, etc.
- `DarkTheme.xaml`: Background=#1E1E1E, Foreground=#E0E0E0, Accent=#60CDFF, CardBg=#2D2D2D, etc.
- `SharedStyles.xaml`: templates de botões, cards, scrollbars (usam `{DynamicResource}`)
- Todas as cores referenciadas via `{DynamicResource BrushName}` — nunca hardcoded

### Cores necessárias (mínimo)
```
WindowBackground, WindowForeground
CardBackground, CardForeground, CardBorder
AccentBrush, AccentHoverBrush
SeekBarBackground, SeekBarProgress, SeekBarThumb
ButtonBackground, ButtonForeground, ButtonHover
SectionHeaderForeground
BadgeBackground, BadgeForeground
PlayerBackground (sempre #000000)
```

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] App abre com tema correto do Windows
- [ ] Mudar tema no Windows → app atualiza sem restart
- [ ] Override manual funciona (light/dark fixo)
- [ ] Todas as views respeitam o tema (sem cores hardcoded)
- [ ] Player sempre fundo preto (independente do tema)

## Dependências
- ST-01 (estrutura WPF)

## Notas
- Subtask simples, pode ser feita em paralelo com ST-03/ST-04
- WPF `DynamicResource` é essencial (não `StaticResource`) para swap em runtime
- Alternativa ao registro: `Microsoft.Win32.SystemEvents.UserPreferenceChanged`
  - Nem sempre dispara para theme change — polling é mais confiável

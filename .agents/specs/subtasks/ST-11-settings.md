# Subtask 11: Settings UI

## Contexto
Spec: `.agents/specs/catra-media-player.md` (Tela 6, AppSettings keys)
Depende de ST-02 (AppSettingsRepository) e ST-04 (navigation).

## Objetivo
Tela de configurações completa: biblioteca, player, DLNA, armazenamento,
tema. Lê/escreve em AppSettings. Aplica mudanças em tempo real.

## Escopo
### Arquivos a Criar
- `src/CATRA.UI/Views/SettingsView.xaml` + `.cs`
- `src/CATRA.UI/ViewModels/SettingsViewModel.cs`
- `src/CATRA.Core/Models/AppSettingsModel.cs` — typed wrapper

### Arquivos a Modificar
- `src/CATRA.UI/Views/HomeView.xaml` — botão ⚙️ navega para Settings
- `src/CATRA.App/App.xaml.cs` — registrar SettingsView

### Arquivos NÃO tocar
- `src/CATRA.Data/` — repos prontos
- `src/CATRA.Services/` — sem lógica nova

## Requisitos Técnicos

### SettingsView (Tela 6)
Seções conforme wireframe da spec:

**Biblioteca:**
- Pasta raiz: TextBox + botão Browse (FolderBrowserDialog)
- Auto-scan: CheckBox

**Player:**
- Skip intro padrão: TextBox numérico (segundos) + label "1:25"
- Tema: ComboBox [Seguir Windows | Light | Dark]

**DLNA:**
- Porta HTTP: TextBox (default "auto")
- Transmitir processado por padrão: CheckBox

**Armazenamento:**
- Pasta processados: TextBox + Browse
- Limpar ao fechar: CheckBox (recomendado)
- Uso atual: Label + botão "Limpar agora"

**Processamento** (placeholder até Fase 2, seção visível mas disabled):
- Janela de episódios: NumericUpDown (default 5)
- Método interpolação: ComboBox [RIFE v4] (disabled)
- Método upscale: ComboBox [FSR 4] (disabled)
- Perfil Local: resolução, fps, bitrate (disabled)
- Perfil DLNA: resolução, fps, bitrate (disabled)

### SettingsViewModel
- Carrega todos os valores de `IAppSettingsRepository` no construtor
- Cada propriedade é bindable com `[ObservableProperty]`
- Save: ao mudar valor → `AppSettingsRepository.Set(key, value)` imediato
- Sem botão "Salvar" — auto-save on change
- Browse folder: `Ookii.Dialogs.Wpf` ou `Microsoft.Win32.OpenFolderDialog` (.NET 8)
- "Limpar agora": deletar conteúdo de processed_folder + mostrar resultado
- Validação: pasta raiz deve existir, skip intro > 0, porta válida ou "auto"

### AppSettingsModel
- Typed properties que mapeiam para keys do DB:
  ```csharp
  public string RootFolder { get => Get("root_folder"); set => Set("root_folder", value); }
  public int WindowSize { get => GetInt("window_size", 5); set => Set("window_size", value); }
  // ... etc para todos os keys da spec
  ```
- Defaults aplicados se key não existe

### Integração
- Mudança de tema → `IThemeService.SetOverride(theme)` (ST-10)
- Mudança de pasta raiz → `ILibraryScanner.ScanAsync()` (novo scan)
- Mudança de skip intro → afeta novos MediaItems (não retroativo)

## Critérios de Sucesso
- [ ] `dotnet build` passa
- [ ] Settings abre via botão ⚙️ na Home
- [ ] Todos os valores carregam corretamente do DB
- [ ] Mudanças persistem imediatamente (fechar e reabrir → valores mantidos)
- [ ] Browse folder funciona
- [ ] Tema muda em tempo real ao selecionar override
- [ ] "Limpar agora" deleta cache e mostra espaço liberado
- [ ] Seção Processamento visível mas disabled (Fase 2)
- [ ] Validação impede valores inválidos
- [ ] Botão voltar funciona

## Dependências
- ST-02 (AppSettingsRepository)
- ST-04 (navigation)
- ST-10 (ThemeService para integração)

## Notas
- `OpenFolderDialog` nativo do .NET 8 WPF: `Microsoft.Win32.OpenFolderDialog`
- Se não disponível: `Ookii.Dialogs.Wpf` via NuGet
- Seção Processamento será habilitada na ST-22

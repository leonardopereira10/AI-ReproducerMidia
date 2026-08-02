using System.Globalization;
using System.IO;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.UI.Navigation;
using CATRA.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CATRA.UI.ViewModels;

/// <summary>
/// Settings screen (Tela 6, ST-11). Loads typed values from
/// <see cref="IAppSettingsRepository"/> (via <see cref="AppSettingsModel"/>) and
/// auto-saves each key on change — there is no "Salvar" button. Theme changes
/// apply in real time through <see cref="IThemeService.SetOverride"/>; changing
/// the library root triggers a fresh <see cref="ILibraryScanner.ScanAsync"/>.
/// Folder picking is abstracted behind <see cref="IFolderPicker"/> so the VM
/// stays headless-testable (no Win32 dialogs here).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsRepository _settings;
    private readonly IThemeService _theme;
    private readonly ILibraryScanner _scanner;
    private readonly IFolderPicker _folderPicker;
    private readonly IAppNavigator _navigator;
    private readonly IDialogService _dialogs;

    // Suppresses auto-save / side effects while the ctor hydrates the bindables.
    private bool _loading = true;

    /// <summary>Creates the view model and loads the current settings.</summary>
    public SettingsViewModel(
        IAppSettingsRepository settings,
        IThemeService theme,
        ILibraryScanner scanner,
        IFolderPicker folderPicker,
        IAppNavigator navigator,
        IDialogService dialogs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        var model = AppSettingsModel.Load(settings);

        // Biblioteca
        _rootFolder = model.RootFolder;
        _autoScan = model.AutoScan;

        // Player
        _skipIntroSec = model.DefaultSkipIntroSec;
        _selectedTheme = model.ThemeOverride;

        // DLNA
        _dlnaHttpPort = model.DlnaHttpPort;
        _dlnaTransmitProcessed = model.DlnaTransmitProcessed;

        // Armazenamento
        _processedFolder = model.ProcessedFolder;
        _cleanupOnClose = model.CleanupOnClose;

        // Processamento (Fase 2 — visível mas desabilitado)
        _windowSize = model.WindowSize;
        _interpMethod = model.InterpMethod;
        _upscaleMethod = model.UpscaleMethod;
        _localWidth = model.LocalTargetWidth;
        _localHeight = model.LocalTargetHeight;
        _localFps = model.LocalTargetFps;
        _localBitrate = model.LocalEncodeBitrateKbps;
        _dlnaWidth = model.DlnaTargetWidth;
        _dlnaHeight = model.DlnaTargetHeight;
        _dlnaFps = model.DlnaTargetFps;
        _dlnaBitrate = model.DlnaEncodeBitrateKbps;

        _loading = false;

        OnPropertyChanged(nameof(SkipIntroDisplay));
        RefreshUsage();
    }

    /// <summary>Theme choices for the combo box (order shown to the user).</summary>
    public IReadOnlyList<AppTheme> ThemeOptions { get; } =
        new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark };

    /// <summary>Interpolation methods (Fase 2).</summary>
    public IReadOnlyList<string> InterpOptions { get; } = new[] { "rife", "fsr3fg" };

    /// <summary>Upscale methods (Fase 2).</summary>
    public IReadOnlyList<string> UpscaleOptions { get; } = new[] { "fsr4", "fsr1" };

    // ---------------------------------------------------------------------
    // Biblioteca
    // ---------------------------------------------------------------------

    /// <summary>Library root folder.</summary>
    [ObservableProperty]
    private string _rootFolder = string.Empty;

    /// <summary>Validation error for <see cref="RootFolder"/> (empty when valid).</summary>
    [ObservableProperty]
    private string _rootFolderError = string.Empty;

    /// <summary>Auto-scan on library changes.</summary>
    [ObservableProperty]
    private bool _autoScan = true;

    // ---------------------------------------------------------------------
    // Player
    // ---------------------------------------------------------------------

    /// <summary>Default intro-skip length in seconds (RN-04 default 85 = 1:25).</summary>
    [ObservableProperty]
    private int _skipIntroSec = 85;

    /// <summary>Validation error for <see cref="SkipIntroSec"/>.</summary>
    [ObservableProperty]
    private string _skipIntroError = string.Empty;

    /// <summary>Formatted intro-skip length ("m:ss").</summary>
    public string SkipIntroDisplay =>
        TimeSpan.FromSeconds(SkipIntroSec).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Selected theme override (Seguir Windows / Light / Dark).</summary>
    [ObservableProperty]
    private AppTheme _selectedTheme = AppTheme.System;

    // ---------------------------------------------------------------------
    // DLNA
    // ---------------------------------------------------------------------

    /// <summary>Embedded HTTP server port ("auto" or 1-65535).</summary>
    [ObservableProperty]
    private string _dlnaHttpPort = "auto";

    /// <summary>Validation error for <see cref="DlnaHttpPort"/>.</summary>
    [ObservableProperty]
    private string _portError = string.Empty;

    /// <summary>Transmit the processed file by default when casting.</summary>
    [ObservableProperty]
    private bool _dlnaTransmitProcessed = true;

    // ---------------------------------------------------------------------
    // Armazenamento
    // ---------------------------------------------------------------------

    /// <summary>Processed-files cache folder.</summary>
    [ObservableProperty]
    private string _processedFolder = string.Empty;

    /// <summary>Clear the cache when the app closes (recommended).</summary>
    [ObservableProperty]
    private bool _cleanupOnClose = true;

    /// <summary>Current cache usage, formatted (e.g. "9.8 GB").</summary>
    [ObservableProperty]
    private string _usageText = "0 B";

    /// <summary>Bottom status line (cleanup feedback, scan errors).</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // ---------------------------------------------------------------------
    // Processamento (Fase 2 — seção visível mas desabilitada)
    // ---------------------------------------------------------------------

    /// <summary>Sliding-window episode count.</summary>
    [ObservableProperty]
    private int _windowSize = 5;

    /// <summary>Interpolation method key.</summary>
    [ObservableProperty]
    private string _interpMethod = "rife";

    /// <summary>Upscale method key.</summary>
    [ObservableProperty]
    private string _upscaleMethod = "fsr4";

    /// <summary>Local profile target width.</summary>
    [ObservableProperty]
    private int _localWidth = 1920;

    /// <summary>Local profile target height.</summary>
    [ObservableProperty]
    private int _localHeight = 1080;

    /// <summary>Local profile target FPS.</summary>
    [ObservableProperty]
    private int _localFps = 135;

    /// <summary>Local profile encode bitrate (kbps).</summary>
    [ObservableProperty]
    private int _localBitrate = 20000;

    /// <summary>DLNA profile target width.</summary>
    [ObservableProperty]
    private int _dlnaWidth = 3840;

    /// <summary>DLNA profile target height.</summary>
    [ObservableProperty]
    private int _dlnaHeight = 2160;

    /// <summary>DLNA profile target FPS.</summary>
    [ObservableProperty]
    private int _dlnaFps = 55;

    /// <summary>DLNA profile encode bitrate (kbps).</summary>
    [ObservableProperty]
    private int _dlnaBitrate = 45000;

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    /// <summary>Goes back to the previous screen.</summary>
    [RelayCommand]
    private void GoBack() => _navigator.GoBack();

    /// <summary>Picks the library root folder.</summary>
    [RelayCommand]
    private void BrowseRootFolder()
    {
        var picked = _folderPicker.PickFolder("Selecione a pasta raiz da biblioteca");
        if (!string.IsNullOrEmpty(picked))
        {
            RootFolder = picked;
        }
    }

    /// <summary>Picks the processed-files cache folder.</summary>
    [RelayCommand]
    private void BrowseProcessedFolder()
    {
        var picked = _folderPicker.PickFolder("Selecione a pasta de processados");
        if (!string.IsNullOrEmpty(picked))
        {
            ProcessedFolder = picked;
        }
    }

    /// <summary>Deletes the processed-files cache and reports the freed space.</summary>
    [RelayCommand]
    private async Task CleanNowAsync()
    {
        var folder = ProcessedFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            UsageText = FormatSize(0);
            StatusMessage = "Nada para limpar.";
            return;
        }

        long freed;
        try
        {
            freed = await Task.Run(() => DeleteContents(folder));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao limpar: {ex.Message}";
            return;
        }

        RefreshUsage();
        StatusMessage = $"Liberados {FormatSize(freed)}.";
    }

    // ---------------------------------------------------------------------
    // Auto-save property handlers
    // ---------------------------------------------------------------------

    partial void OnRootFolderChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            RootFolderError = string.Empty;
            _settings.Set(AppSettingsModel.RootFolderKey, string.Empty);
            return;
        }

        if (!Directory.Exists(value))
        {
            RootFolderError = "A pasta não existe.";
            return; // invalid: do not persist, do not scan
        }

        RootFolderError = string.Empty;
        _settings.Set(AppSettingsModel.RootFolderKey, value);
        _ = RescanAsync(value);
    }

    partial void OnAutoScanChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Set(AppSettingsModel.AutoScanKey, ToBoolString(value));
        }
    }

    partial void OnSkipIntroSecChanged(int value)
    {
        OnPropertyChanged(nameof(SkipIntroDisplay));
        if (_loading)
        {
            return;
        }

        if (value <= 0)
        {
            SkipIntroError = "Deve ser maior que zero.";
            return; // invalid: do not persist
        }

        SkipIntroError = string.Empty;
        _settings.Set(AppSettingsModel.DefaultSkipIntroSecKey, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (!_loading)
        {
            // Persists theme_override and applies the theme in real time.
            _theme.SetOverride(value);
        }
    }

    partial void OnDlnaHttpPortChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        if (!IsValidPort(value))
        {
            PortError = "Use 'auto' ou um número entre 1 e 65535.";
            return; // invalid: do not persist
        }

        PortError = string.Empty;
        _settings.Set(AppSettingsModel.DlnaHttpPortKey, value.Trim());
    }

    partial void OnDlnaTransmitProcessedChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Set(AppSettingsModel.DlnaTransmitProcessedKey, ToBoolString(value));
        }
    }

    partial void OnProcessedFolderChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _settings.Set(AppSettingsModel.ProcessedFolderKey, value ?? string.Empty);
        RefreshUsage();
    }

    partial void OnCleanupOnCloseChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Set(AppSettingsModel.CleanupOnCloseKey, ToBoolString(value));
        }
    }

    // --- Processamento (Fase 2): persistem, embora a seção fique desabilitada ---

    partial void OnWindowSizeChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.WindowSizeKey, value);

    partial void OnInterpMethodChanged(string value)
        => SaveStringIfNotLoading(AppSettingsModel.InterpMethodKey, value);

    partial void OnUpscaleMethodChanged(string value)
        => SaveStringIfNotLoading(AppSettingsModel.UpscaleMethodKey, value);

    partial void OnLocalWidthChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalTargetWidthKey, value);

    partial void OnLocalHeightChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalTargetHeightKey, value);

    partial void OnLocalFpsChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalTargetFpsKey, value);

    partial void OnLocalBitrateChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalEncodeBitrateKey, value);

    partial void OnDlnaWidthChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaTargetWidthKey, value);

    partial void OnDlnaHeightChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaTargetHeightKey, value);

    partial void OnDlnaFpsChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaTargetFpsKey, value);

    partial void OnDlnaBitrateChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaEncodeBitrateKey, value);

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void SaveIntIfNotLoading(string key, int value)
    {
        if (!_loading)
        {
            _settings.Set(key, value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void SaveStringIfNotLoading(string key, string value)
    {
        if (!_loading)
        {
            _settings.Set(key, value ?? string.Empty);
        }
    }

    private static string ToBoolString(bool value) => value ? "true" : "false";

    private static bool IsValidPort(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535;
    }

    /// <summary>Recomputes <see cref="UsageText"/> from the processed folder.</summary>
    public void RefreshUsage()
    {
        var folder = ProcessedFolder;
        long size = 0;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                size = GetDirectorySize(folder);
            }
            catch
            {
                size = 0;
            }
        }

        UsageText = FormatSize(size);
    }

    private async Task RescanAsync(string rootFolder)
    {
        try
        {
            await _scanner.ScanAsync(rootFolder);
            StatusMessage = "Biblioteca re-escaneada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao escanear: {ex.Message}";
        }
    }

    private static long GetDirectorySize(string folder)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore files that vanish / are locked mid-enumeration.
            }
        }

        return total;
    }

    private static long DeleteContents(string folder)
    {
        long freed = GetDirectorySize(folder);

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort: skip locked/in-use files.
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort: skip non-empty/locked directories.
            }
        }

        return freed;
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[0]}"
            : size.ToString("0.## ", CultureInfo.InvariantCulture) + units[unit];
    }
}

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
    private readonly INativeBridge _nativeBridge;

    // Suppresses auto-save / side effects while the ctor hydrates the bindables.
    private bool _loading = true;

    /// <summary>Creates the view model and loads the current settings.</summary>
    public SettingsViewModel(
        IAppSettingsRepository settings,
        IThemeService theme,
        ILibraryScanner scanner,
        IFolderPicker folderPicker,
        IAppNavigator navigator,
        IDialogService dialogs,
        INativeBridge nativeBridge)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _nativeBridge = nativeBridge ?? throw new ArgumentNullException(nameof(nativeBridge));

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
        _maxParallelJobs = model.MaxParallelJobs;
        _localWidth = model.LocalTargetWidth;
        _localHeight = model.LocalTargetHeight;
        _localFps = model.LocalTargetFps;
        _localBitrate = model.LocalEncodeBitrateKbps;
        _dlnaWidth = model.DlnaTargetWidth;
        _dlnaHeight = model.DlnaTargetHeight;
        _dlnaFps = model.DlnaTargetFps;
        _dlnaBitrate = model.DlnaEncodeBitrateKbps;

        // Text bindings for resolution/FPS (free typing, commit on LostFocus)
        _localWidthText = model.LocalTargetWidth.ToString(CultureInfo.InvariantCulture);
        _localHeightText = model.LocalTargetHeight.ToString(CultureInfo.InvariantCulture);
        _localFpsText = model.LocalTargetFps.ToString(CultureInfo.InvariantCulture);
        _dlnaWidthText = model.DlnaTargetWidth.ToString(CultureInfo.InvariantCulture);
        _dlnaHeightText = model.DlnaTargetHeight.ToString(CultureInfo.InvariantCulture);
        _dlnaFpsText = model.DlnaTargetFps.ToString(CultureInfo.InvariantCulture);

        _loading = false;

        // Story 01 fix: probe real FFX availability via the native bridge.
        // N2 fix: use IsFsr4Available() when bridge is initialized (definitive,
        // uses the decoder's adapter), else fall back to IsFfxAvailable() (pre-init
        // probe, uses a transient DX12 device on the default adapter).
        // This replaces the hardcoded default with the actual system state.
        try
        {
            _fsr4Available = _nativeBridge.IsInitialized
                ? _nativeBridge.IsFsr4Available()
                : _nativeBridge.IsFfxAvailable();
        }
        catch
        {
            // N3: if the native bridge throws during probe (e.g. EntryPointNotFoundException
            // on a stale DLL), degrade to unavailable without crashing.
            _fsr4Available = false;
        }

        OnPropertyChanged(nameof(SkipIntroDisplay));
        OnPropertyChanged(nameof(LocalSizeEstimate));
        OnPropertyChanged(nameof(DlnaSizeEstimate));
        RefreshUpscaleWarning();
        RefreshUsage();
    }

    /// <summary>Theme choices for the combo box (order shown to the user).</summary>
    public IReadOnlyList<AppTheme> ThemeOptions { get; } =
        new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark };

    /// <summary>Interpolation methods (Fase 2).</summary>
    public IReadOnlyList<string> InterpOptions { get; } = new[] { "rife", "fsr3fg" };

    /// <summary>Upscale methods (Fase 2).</summary>
    public IReadOnlyList<string> UpscaleOptions { get; } = new[] { "fsr4", "fsr1" };

    // --- Processamento: validation ranges (ST-22) ---
    internal const int MinWindowSize = 1;
    internal const int MaxWindowSize = 20;
    internal const int MinFps = 24;
    internal const int MaxFps = 240;
    internal const int MinWidth = 640;
    internal const int MaxWidth = 7680;
    internal const int MinHeight = 360;
    internal const int MaxHeight = 4320;
    internal const int MinBitrateKbps = 1000;
    internal const int MaxBitrateKbps = 200000;

    /// <summary>Reference episode length used for the size estimate (22 min).</summary>
    internal const double EstimateEpisodeSeconds = 22 * 60;

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
    // Processamento (ST-22 — habilitado, persiste e valida em tempo real)
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

    /// <summary>Maximum number of videos to process simultaneously (1-4 recommended).</summary>
    [ObservableProperty]
    private int _maxParallelJobs = 1;

    /// <summary>Local profile target width.</summary>
    [ObservableProperty]
    private int _localWidth = 1920;

    /// <summary>Local profile target height.</summary>
    [ObservableProperty]
    private int _localHeight = 1080;

    /// <summary>Local profile target FPS.</summary>
    [ObservableProperty]
    private int _localFps = 60;

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

    // --- Text bindings for resolution/FPS fields (free typing, commit on LostFocus) ---

    [ObservableProperty]
    private string _localWidthText = string.Empty;

    [ObservableProperty]
    private string _localHeightText = string.Empty;

    [ObservableProperty]
    private string _localFpsText = string.Empty;

    [ObservableProperty]
    private string _dlnaWidthText = string.Empty;

    [ObservableProperty]
    private string _dlnaHeightText = string.Empty;

    [ObservableProperty]
    private string _dlnaFpsText = string.Empty;

    [ObservableProperty]
    private string _localWidthError = string.Empty;

    [ObservableProperty]
    private string _localHeightError = string.Empty;

    [ObservableProperty]
    private string _localFpsError = string.Empty;

    [ObservableProperty]
    private string _dlnaWidthError = string.Empty;

    [ObservableProperty]
    private string _dlnaHeightError = string.Empty;

    [ObservableProperty]
    private string _dlnaFpsError = string.Empty;

    /// <summary>First validation error for the Local profile (empty when all valid).</summary>
    public string LocalValidationError =>
        !string.IsNullOrEmpty(LocalWidthError) ? LocalWidthError :
        !string.IsNullOrEmpty(LocalHeightError) ? LocalHeightError :
        !string.IsNullOrEmpty(LocalFpsError) ? LocalFpsError :
        string.Empty;

    /// <summary>First validation error for the DLNA profile (empty when all valid).</summary>
    public string DlnaValidationError =>
        !string.IsNullOrEmpty(DlnaWidthError) ? DlnaWidthError :
        !string.IsNullOrEmpty(DlnaHeightError) ? DlnaHeightError :
        !string.IsNullOrEmpty(DlnaFpsError) ? DlnaFpsError :
        string.Empty;

    /// <summary>
    /// Whether FSR 4 is available on this machine. Default: <c>false</c>
    /// (fail-safe — N5 fix). The constructor probes the native bridge and
    /// updates this value. When the probe fails or the bridge is unavailable,
    /// FSR 4 stays marked as unavailable and the UI shows a suggestion to
    /// use FSR 1 instead.
    /// </summary>
    [ObservableProperty]
    private bool _fsr4Available = false;

    /// <summary>
    /// Warning shown when FSR 4 is selected but unavailable (empty when OK).
    /// </summary>
    [ObservableProperty]
    private string _upscaleWarning = string.Empty;

    /// <summary>Estimated output size per 22-min episode for the Local profile.</summary>
    public string LocalSizeEstimate => FormatSizeEstimate(LocalBitrate);

    /// <summary>Estimated output size per 22-min episode for the DLNA profile.</summary>
    public string DlnaSizeEstimate => FormatSizeEstimate(DlnaBitrate);

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

    // --- Processamento (ST-22): clamp + persistência imediata ---

    partial void OnWindowSizeChanged(int value)
        => ClampAndSave(ref value, MinWindowSize, MaxWindowSize, v => WindowSize = v,
            AppSettingsModel.WindowSizeKey);

    partial void OnInterpMethodChanged(string value)
        => SaveStringIfNotLoading(AppSettingsModel.InterpMethodKey, value);

    partial void OnUpscaleMethodChanged(string value)
    {
        RefreshUpscaleWarning();
        SaveStringIfNotLoading(AppSettingsModel.UpscaleMethodKey, value);
    }

    partial void OnMaxParallelJobsChanged(int value)
    {
        // Clamp to valid range (1-8 parallel jobs)
        if (value < 1) value = 1;
        if (value > 8) value = 8;
        if (value != MaxParallelJobs)
        {
            MaxParallelJobs = value;
            return; // re-entrant call will handle save
        }
        SaveIntIfNotLoading(AppSettingsModel.MaxParallelJobsKey, value);
    }

    partial void OnFsr4AvailableChanged(bool value)
        => RefreshUpscaleWarning();

    partial void OnLocalWidthChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalTargetWidthKey, value);

    partial void OnLocalHeightChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalTargetHeightKey, value);

    partial void OnLocalFpsChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.LocalTargetFpsKey, value);

    partial void OnLocalBitrateChanged(int value)
    {
        OnPropertyChanged(nameof(LocalSizeEstimate));
        ClampAndSave(ref value, MinBitrateKbps, MaxBitrateKbps, v => LocalBitrate = v,
            AppSettingsModel.LocalEncodeBitrateKey);
    }

    partial void OnDlnaWidthChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaTargetWidthKey, value);

    partial void OnDlnaHeightChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaTargetHeightKey, value);

    partial void OnDlnaFpsChanged(int value)
        => SaveIntIfNotLoading(AppSettingsModel.DlnaTargetFpsKey, value);

    partial void OnDlnaBitrateChanged(int value)
    {
        OnPropertyChanged(nameof(DlnaSizeEstimate));
        ClampAndSave(ref value, MinBitrateKbps, MaxBitrateKbps, v => DlnaBitrate = v,
            AppSettingsModel.DlnaEncodeBitrateKey);
    }

    // --- Text-change handlers (validate on every keystroke, apply if valid) ---

    partial void OnLocalWidthTextChanged(string value)
        => ValidateAndApplyInt(value, MinWidth, MaxWidth, v => LocalWidth = v, e => LocalWidthError = e);

    partial void OnLocalHeightTextChanged(string value)
        => ValidateAndApplyInt(value, MinHeight, MaxHeight, v => LocalHeight = v, e => LocalHeightError = e);

    partial void OnLocalFpsTextChanged(string value)
        => ValidateAndApplyInt(value, MinFps, MaxFps, v => LocalFps = v, e => LocalFpsError = e);

    partial void OnDlnaWidthTextChanged(string value)
        => ValidateAndApplyInt(value, MinWidth, MaxWidth, v => DlnaWidth = v, e => DlnaWidthError = e);

    partial void OnDlnaHeightTextChanged(string value)
        => ValidateAndApplyInt(value, MinHeight, MaxHeight, v => DlnaHeight = v, e => DlnaHeightError = e);

    partial void OnDlnaFpsTextChanged(string value)
        => ValidateAndApplyInt(value, MinFps, MaxFps, v => DlnaFps = v, e => DlnaFpsError = e);

    // --- Error-change handlers (refresh aggregated validation messages) ---

    partial void OnLocalWidthErrorChanged(string value) => OnPropertyChanged(nameof(LocalValidationError));
    partial void OnLocalHeightErrorChanged(string value) => OnPropertyChanged(nameof(LocalValidationError));
    partial void OnLocalFpsErrorChanged(string value) => OnPropertyChanged(nameof(LocalValidationError));
    partial void OnDlnaWidthErrorChanged(string value) => OnPropertyChanged(nameof(DlnaValidationError));
    partial void OnDlnaHeightErrorChanged(string value) => OnPropertyChanged(nameof(DlnaValidationError));
    partial void OnDlnaFpsErrorChanged(string value) => OnPropertyChanged(nameof(DlnaValidationError));

    // --- Commit commands (LostFocus: clamp + restore text) ---

    [RelayCommand]
    public void CommitLocalWidth()
        => CommitIntField(LocalWidthText, MinWidth, MaxWidth, LocalWidth,
            v => LocalWidth = v, t => LocalWidthText = t, e => LocalWidthError = e);

    [RelayCommand]
    public void CommitLocalHeight()
        => CommitIntField(LocalHeightText, MinHeight, MaxHeight, LocalHeight,
            v => LocalHeight = v, t => LocalHeightText = t, e => LocalHeightError = e);

    [RelayCommand]
    public void CommitLocalFps()
        => CommitIntField(LocalFpsText, MinFps, MaxFps, LocalFps,
            v => LocalFps = v, t => LocalFpsText = t, e => LocalFpsError = e);

    [RelayCommand]
    public void CommitDlnaWidth()
        => CommitIntField(DlnaWidthText, MinWidth, MaxWidth, DlnaWidth,
            v => DlnaWidth = v, t => DlnaWidthText = t, e => DlnaWidthError = e);

    [RelayCommand]
    public void CommitDlnaHeight()
        => CommitIntField(DlnaHeightText, MinHeight, MaxHeight, DlnaHeight,
            v => DlnaHeight = v, t => DlnaHeightText = t, e => DlnaHeightError = e);

    [RelayCommand]
    public void CommitDlnaFps()
        => CommitIntField(DlnaFpsText, MinFps, MaxFps, DlnaFps,
            v => DlnaFps = v, t => DlnaFpsText = t, e => DlnaFpsError = e);

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>Persists an int setting immediately (no clamping).</summary>
    private void SaveIntIfNotLoading(string key, int value)
    {
        if (!_loading)
        {
            _settings.Set(key, value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Parses <paramref name="text"/> as an int. If valid and in [min, max],
    /// clears the error and applies via <paramref name="setInt"/> (which
    /// triggers persistence). Otherwise sets the error message.
    /// </summary>
    private void ValidateAndApplyInt(string? text, int min, int max,
        Action<int> setInt, Action<string> setError)
    {
        if (_loading) return;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= min && parsed <= max)
        {
            setError(string.Empty);
            setInt(parsed);
        }
        else
        {
            setError($"Entre {min} e {max}.");
        }
    }

    /// <summary>
    /// Called on LostFocus: if the text is unparseable, restores the last
    /// committed int value; if out-of-range, clamps and persists the clamped
    /// value. Always clears the error and syncs the text.
    /// </summary>
    private void CommitIntField(string? text, int min, int max, int currentValue,
        Action<int> setInt, Action<string> setText, Action<string> setError)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            setText(currentValue.ToString(CultureInfo.InvariantCulture));
            setError(string.Empty);
            return;
        }

        var clamped = Math.Clamp(parsed, min, max);
        if (clamped != currentValue)
        {
            setInt(clamped);
        }
        setText(clamped.ToString(CultureInfo.InvariantCulture));
        setError(string.Empty);
    }

    /// <summary>
    /// Clamps <paramref name="value"/> into [min, max]. Out-of-range values are
    /// written back through <paramref name="setter"/> (which re-enters the
    /// change handler with the clamped value and persists it); in-range values
    /// are persisted immediately.
    /// </summary>
    private void ClampAndSave(ref int value, int min, int max, Action<int> setter, string key)
    {
        if (_loading)
        {
            return;
        }

        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
        {
            setter(clamped);
            return;
        }

        _settings.Set(key, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Recomputes <see cref="UpscaleWarning"/> from the current state.</summary>
    private void RefreshUpscaleWarning()
    {
        UpscaleWarning = UpscaleMethod == "fsr4" && !Fsr4Available
            ? "FSR 4 não está disponível nesta máquina. Sugestão: use FSR 1."
            : string.Empty;
    }

    /// <summary>
    /// Estimated output size in GB for one 22-minute episode at the given
    /// bitrate: 22min × bitrate / 8 (decimal GB, matches spec examples).
    /// </summary>
    internal static double EstimateGbPerEpisode(int bitrateKbps)
        => bitrateKbps * 1000d / 8d * EstimateEpisodeSeconds / 1e9;

    private static string FormatSizeEstimate(int bitrateKbps)
        => $"~{EstimateGbPerEpisode(bitrateKbps).ToString("0.#", CultureInfo.InvariantCulture)} GB por episódio (22min)";

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

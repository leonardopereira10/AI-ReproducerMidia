using CATRA.Core.Enums;
using CATRA.Core.Interfaces;

namespace CATRA.Core.Models;

/// <summary>
/// Typed wrapper over the key/value <c>AppSettings</c> table (spec "AppSettings
/// keys"). Exposes strongly-typed properties with spec defaults plus the
/// canonical key names, and can hydrate itself from an
/// <see cref="IAppSettingsRepository"/>. The Settings screen (Tela 6 / ST-11)
/// reads through this model and writes individual keys back on change.
/// </summary>
public sealed class AppSettingsModel
{
    // --- Persisted keys (seeded by DatabaseInitializer) ---
    public const string RootFolderKey = "root_folder";
    public const string ProcessedFolderKey = "processed_folder";
    public const string WindowSizeKey = "window_size";
    public const string CleanupOnCloseKey = "cleanup_on_close";
    public const string LocalTargetFpsKey = "local_target_fps";
    public const string LocalTargetWidthKey = "local_target_width";
    public const string LocalTargetHeightKey = "local_target_height";
    public const string LocalEncodeBitrateKey = "local_encode_bitrate_kbps";
    public const string DlnaTargetFpsKey = "dlna_target_fps";
    public const string DlnaTargetWidthKey = "dlna_target_width";
    public const string DlnaTargetHeightKey = "dlna_target_height";
    public const string DlnaEncodeBitrateKey = "dlna_encode_bitrate_kbps";
    public const string InterpMethodKey = "interp_method";
    public const string UpscaleMethodKey = "upscale_method";
    public const string DefaultSkipIntroSecKey = "default_skip_intro_sec";
    public const string ThemeOverrideKey = "theme_override";
    public const string MaxParallelJobsKey = "max_parallel_jobs";
    public const string WebPanelPortKey = "web_panel_port";

    // --- UI-only keys (not seeded; created on first change, read with defaults) ---
    public const string AutoScanKey = "auto_scan";
    public const string DlnaHttpPortKey = "dlna_http_port";
    public const string DlnaTransmitProcessedKey = "dlna_transmit_processed";

    // --- Biblioteca ---
    public string RootFolder { get; set; } = string.Empty;
    public bool AutoScan { get; set; } = true;

    // --- Armazenamento ---
    public string ProcessedFolder { get; set; } = string.Empty;
    public bool CleanupOnClose { get; set; } = true;

    // --- Player ---
    public int DefaultSkipIntroSec { get; set; } = 85;
    public AppTheme ThemeOverride { get; set; } = AppTheme.System;

    // --- Processamento Paralelo ---
    /// <summary>
    /// Maximum number of videos to process simultaneously. Default is 1 (sequential).
    /// With GPU underutilization (14% observed), values 2-4 can improve throughput
    /// without increasing per-video processing time.
    /// </summary>
    public int MaxParallelJobs { get; set; } = 1;

    // --- Web Panel ---
    public int WebPanelPort { get; set; } = 5050;

    // --- DLNA ---
    public string DlnaHttpPort { get; set; } = "auto";
    public bool DlnaTransmitProcessed { get; set; } = true;

    // --- Processamento (Fase 2; visível mas desabilitado na UI) ---
    public int WindowSize { get; set; } = 5;
    public string InterpMethod { get; set; } = "rife";
    // D-PO-3: FSR 1 (EASU) is the default for export AND playback (plan Risco 3 / A6);
// FSR 4 is opt-in.
public string UpscaleMethod { get; set; } = "fsr1";
    public int LocalTargetWidth { get; set; } = 1920;
    public int LocalTargetHeight { get; set; } = 1080;
    public int LocalTargetFps { get; set; } = 60;
    public int LocalEncodeBitrateKbps { get; set; } = 20000;
    public int DlnaTargetWidth { get; set; } = 3840;
    public int DlnaTargetHeight { get; set; } = 2160;
    public int DlnaTargetFps { get; set; } = 55;
    public int DlnaEncodeBitrateKbps { get; set; } = 45000;

    /// <summary>
    /// Reads every known key from <paramref name="repository"/>, falling back to
    /// the spec default when a key is absent or unparseable.
    /// </summary>
    public static AppSettingsModel Load(IAppSettingsRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var model = new AppSettingsModel();
        var all = repository.GetAll();

        model.RootFolder = GetString(all, RootFolderKey, model.RootFolder);
        model.ProcessedFolder = GetString(all, ProcessedFolderKey, model.ProcessedFolder);
        model.AutoScan = GetBool(all, AutoScanKey, model.AutoScan);
        model.CleanupOnClose = GetBool(all, CleanupOnCloseKey, model.CleanupOnClose);
        model.DlnaTransmitProcessed = GetBool(all, DlnaTransmitProcessedKey, model.DlnaTransmitProcessed);
        model.DlnaHttpPort = GetString(all, DlnaHttpPortKey, model.DlnaHttpPort);

        model.DefaultSkipIntroSec = GetInt(all, DefaultSkipIntroSecKey, model.DefaultSkipIntroSec);
        model.WindowSize = GetInt(all, WindowSizeKey, model.WindowSize);

        model.LocalTargetFps = GetInt(all, LocalTargetFpsKey, model.LocalTargetFps);
        model.LocalTargetWidth = GetInt(all, LocalTargetWidthKey, model.LocalTargetWidth);
        model.LocalTargetHeight = GetInt(all, LocalTargetHeightKey, model.LocalTargetHeight);
        model.LocalEncodeBitrateKbps = GetInt(all, LocalEncodeBitrateKey, model.LocalEncodeBitrateKbps);

        model.DlnaTargetFps = GetInt(all, DlnaTargetFpsKey, model.DlnaTargetFps);
        model.DlnaTargetWidth = GetInt(all, DlnaTargetWidthKey, model.DlnaTargetWidth);
        model.DlnaTargetHeight = GetInt(all, DlnaTargetHeightKey, model.DlnaTargetHeight);
        model.DlnaEncodeBitrateKbps = GetInt(all, DlnaEncodeBitrateKey, model.DlnaEncodeBitrateKbps);

        model.InterpMethod = GetString(all, InterpMethodKey, model.InterpMethod);
        model.UpscaleMethod = GetString(all, UpscaleMethodKey, model.UpscaleMethod);
        model.ThemeOverride = ParseTheme(GetString(all, ThemeOverrideKey, "system"));
        model.MaxParallelJobs = Math.Max(1, GetInt(all, MaxParallelJobsKey, model.MaxParallelJobs));
        model.WebPanelPort = GetInt(all, WebPanelPortKey, model.WebPanelPort);

        return model;
    }

    /// <summary>Maps a stored theme string to the enum (unknown → System).</summary>
    public static AppTheme ParseTheme(string? raw) => raw switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System
    };

    /// <summary>Maps a theme enum to its stored string.</summary>
    public static string ThemeToString(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system"
    };

    private static string GetString(IReadOnlyDictionary<string, string> all, string key, string fallback)
        => all.TryGetValue(key, out var value) ? value : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> all, string key, bool fallback)
        => all.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> all, string key, int fallback)
        => all.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
}

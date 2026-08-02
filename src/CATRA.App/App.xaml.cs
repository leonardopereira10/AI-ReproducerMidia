using System.IO;
using System.Windows;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Navigation;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services;
using CATRA.Services.Casting;
using CATRA.Services.Library;
using CATRA.Services.Metadata;
using CATRA.Services.Playback;
using CATRA.Services.Processing;
using CATRA.UI.Navigation;
using CATRA.UI.Services;
using CATRA.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CATRA.App;

/// <summary>
/// Application entry point. Builds the DI host and shows the shell window.
/// </summary>
public partial class App : Application
{
    private static IHost? _host;

    private static readonly Uri LightThemeUri =
        new("pack://application:,,,/CATRA.UI;component/Themes/LightTheme.xaml");

    private static readonly Uri DarkThemeUri =
        new("pack://application:,,,/CATRA.UI;component/Themes/DarkTheme.xaml");

    /// <summary>
    /// Application-wide service provider (composition root).
    /// </summary>
    public static IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("DI host is not initialized.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureFfmpeg();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Domain services
                services.AddSingleton<ILibraryService, LibraryService>();

                // Watch state business layer (ST-07): RN-02 threshold + auto-mark,
                // RN-03 "Continuar Assistindo", RN-08 original-file timestamps.
                services.AddSingleton<IWatchStateService, WatchStateService>();

                // Playback pipeline (ST-05): FFmpeg decode + DX11 render + WASAPI audio.
                // Decoders/renderers are transient so the engine gets fresh instances per
                // open media; the engine itself is a singleton orchestrator.
                services.AddTransient<IVideoDecoder, VideoDecoder>();
                services.AddTransient<IAudioDecoder, AudioDecoder>();
                services.AddTransient<IVideoRenderer, VideoRenderer>();
                services.AddTransient<IAudioRenderer, AudioRenderer>();
                services.AddSingleton<IPlaybackEngine>(sp => new PlaybackEngine(
                    () => sp.GetRequiredService<IVideoDecoder>(),
                    () => sp.GetRequiredService<IAudioDecoder>(),
                    () => sp.GetRequiredService<IVideoRenderer>(),
                    () => sp.GetRequiredService<IAudioRenderer>()));

                // DLNA casting (ST-08): SSDP discovery, embedded Kestrel media
                // server (Range requests), AVTransport/RenderingControl SOAP clients
                // and the orchestrator (1s position polling by default).
                services.AddSingleton<IDlnaDiscoveryService, DlnaDiscoveryService>();
                services.AddSingleton<IMediaHttpServer>(_ => new MediaHttpServer());
                services.AddSingleton<IAvTransportClient, AvTransportClient>();
                services.AddSingleton<IRenderingControlClient, RenderingControlClient>();
                services.AddSingleton<ICastingService, CastingService>();

                // Library scanning (ST-03): parser, probe, scanner, watcher.
                // IMediaProbeService is ffprobe-based; the real binary is bundled
                // later (ST-05, FFmpeg.AutoGen) — until then probing returns null.
                services.AddSingleton<IFilenameParser, FilenameParser>();
                services.AddSingleton<IMediaProbeService, FfprobeMediaProbeService>();
                services.AddSingleton<ILibraryScanner, LibraryScanner>();
                services.AddSingleton<ILibraryWatcher, LibraryWatcher>();

                // Native GPU bridge (ST-12): P/Invoke wrapper over catra-gpu.dll
                // (FSR 4 / RIFE / AMF, delivered in ST-13..ST-16). Degrades
                // gracefully when the native DLL has not been built yet.
                services.AddSingleton<INativeBridge, NativeBridge>();

                // Pre-processing pipeline (ST-17): decode → interp → upscale → encode
                // → mux orchestrator over the native bridge + injectable decoder/muxer.
                // The decoder is transient (a fresh FFmpeg decoder per episode); the
                // pipeline is a singleton that creates one via the factory per episode.
                services.AddTransient<IFrameDecoder, FrameDecoder>();
                services.AddSingleton<IAudioMuxer, AudioMuxer>();
                services.AddSingleton<IProcessingPipeline>(sp => new ProcessingPipeline(
                    sp.GetRequiredService<INativeBridge>(),
                    () => sp.GetRequiredService<IFrameDecoder>(),
                    sp.GetRequiredService<IAudioMuxer>()));

                // Pre-processing queue + sliding window (ST-18, RF-03, RN-10): a
                // dedicated background worker drains a thread-safe channel of jobs
                // through the pipeline; the sliding window keeps the next window_size
                // unwatched episodes queued and rotates as episodes are watched.
                // ProcessedFileUsage is the conservative "in use" probe (RF-04: never
                // delete a file being played/streamed).
                services.AddSingleton<IProcessedFileUsage, ProcessedFileUsage>();
                services.AddSingleton<IProcessingQueueService, ProcessingQueueService>();
                services.AddSingleton<ISlidingWindowService, SlidingWindowService>();

                // Thumbnails / covers (ST-09, RF-08, RN-05): ffmpeg CLI frame
                // grabber behind an injectable extractor + caching service. The
                // binary is not bundled yet, so extraction degrades to the UI
                // placeholder until ffmpeg is available.
                services.AddSingleton<IThumbnailExtractor, FfmpegThumbnailExtractor>();
                services.AddSingleton<IThumbnailService, ThumbnailService>();

                // Navigation
                services.AddSingleton<FrameNavigationService>();
                services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<FrameNavigationService>());
                services.AddSingleton<IAppNavigator, AppNavigator>();

                // UI services + view models (ST-04: Home / Detail, ST-06: Player)
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<IFolderPicker, FolderPicker>();
                services.AddTransient<HomeViewModel>();
                services.AddTransient<MediaDetailViewModel>();
                services.AddTransient<ProcessingQueueViewModel>();
                services.AddTransient<PlayerViewModel>();
                services.AddTransient<SettingsViewModel>();

                // Theme
                services.AddSingleton<ISystemThemeDetector, SystemThemeDetector>();
                services.AddSingleton<IThemeService, ThemeService>();

                // Data layer
                services.AddSingleton(_ => new DatabaseConnection(DatabaseLocation.Default));
                services.AddSingleton<DatabaseInitializer>();
                services.AddSingleton<ICategoryRepository, CategoryRepository>();
                services.AddSingleton<IMediaItemRepository, MediaItemRepository>();
                services.AddSingleton<IEpisodeRepository, EpisodeRepository>();
                services.AddSingleton<IProcessedFileRepository, ProcessedFileRepository>();
                services.AddSingleton<IProcessJobRepository, ProcessJobRepository>();
                services.AddSingleton<IWatchStateRepository, WatchStateRepository>();
                services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
            })
            .Build();

        await _host.StartAsync();

        // Create/migrate the SQLite schema and seed default settings.
        _host.Services.GetRequiredService<DatabaseInitializer>().Initialize();

        // Start the pre-processing queue worker (ST-18): runs crash recovery
        // (processing → failed) then the background processing loop.
        await _host.Services.GetRequiredService<IProcessingQueueService>().StartAsync();

        // Start the embedded DLNA media server (ST-08): Kestrel on an ephemeral
        // LAN port, ready before the first cast. The firewall rule attempt is
        // best-effort (needs elevation; logs the manual instruction otherwise).
        await _host.Services.GetRequiredService<IMediaHttpServer>().StartAsync();
        _ = Task.Run(() => FirewallHelper.TryEnsureFirewallRule());

        // Initialize theme: apply current theme and start polling.
        var themeService = _host.Services.GetRequiredService<IThemeService>();
        ApplyTheme(themeService.CurrentTheme);
        themeService.ThemeChanged += OnThemeChanged;
        themeService.Start();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Stop the pre-processing queue worker before tearing down the host (ST-18).
            var queue = _host.Services.GetService<IProcessingQueueService>();
            if (queue is not null)
            {
                await queue.StopAsync();
            }

            // Stop the DLNA media server before tearing down the host (ST-08).
            var mediaServer = _host.Services.GetService<IMediaHttpServer>();
            if (mediaServer is not null)
            {
                await mediaServer.StopAsync();
            }

            var themeService = _host.Services.GetService<IThemeService>();
            if (themeService is not null)
            {
                themeService.ThemeChanged -= OnThemeChanged;
                themeService.Stop();
            }

            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Points FFmpeg.AutoGen at the bundled shared libraries (ST-05). When the
    /// <c>lib/ffmpeg</c> folder is absent (e.g. fresh clone before running
    /// <c>scripts/download-ffmpeg.ps1</c>) the default loader search path is kept.
    /// </summary>
    private static void ConfigureFfmpeg()
    {
        string ffmpegDir = Path.Combine(AppContext.BaseDirectory, "lib", "ffmpeg");
        if (Directory.Exists(ffmpegDir))
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = ffmpegDir;
        }
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.Invoke(() => ApplyTheme(theme));
    }

    private static void ApplyTheme(AppTheme theme)
    {
        var targetUri = theme == AppTheme.Light ? LightThemeUri : DarkThemeUri;
        var mergedDictionaries = Current.Resources.MergedDictionaries;

        // Find and replace the existing theme dictionary (LightTheme or DarkTheme).
        ResourceDictionary? existing = null;
        foreach (var dict in mergedDictionaries)
        {
            if (dict.Source is not null &&
                (dict.Source == LightThemeUri || dict.Source == DarkThemeUri))
            {
                existing = dict;
                break;
            }
        }

        if (existing is not null)
        {
            mergedDictionaries.Remove(existing);
        }

        mergedDictionaries.Add(new ResourceDictionary { Source = targetUri });
    }
}

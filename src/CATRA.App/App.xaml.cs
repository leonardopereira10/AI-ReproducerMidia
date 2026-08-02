using System.Windows;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Navigation;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Library;
using CATRA.UI.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CATRA.App;

/// <summary>
/// Application entry point. Builds the DI host and shows the shell window.
/// </summary>
public partial class App : Application
{
    private static IHost? _host;

    /// <summary>
    /// Application-wide service provider (composition root).
    /// </summary>
    public static IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("DI host is not initialized.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Domain services
                services.AddSingleton<ILibraryService, LibraryService>();

                // Library scanning (ST-03): parser, probe, scanner, watcher.
                // IMediaProbeService is ffprobe-based; the real binary is bundled
                // later (ST-05, FFmpeg.AutoGen) — until then probing returns null.
                services.AddSingleton<IFilenameParser, FilenameParser>();
                services.AddSingleton<IMediaProbeService, FfprobeMediaProbeService>();
                services.AddSingleton<ILibraryScanner, LibraryScanner>();
                services.AddSingleton<ILibraryWatcher, LibraryWatcher>();

                // Navigation
                services.AddSingleton<FrameNavigationService>();
                services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<FrameNavigationService>());

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

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }
}

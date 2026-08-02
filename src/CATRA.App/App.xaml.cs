using System.Windows;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Navigation;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services;
using CATRA.Services.Library;
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
                services.AddSingleton<IAppNavigator, AppNavigator>();

                // UI services + view models (ST-04: Home / Detail screens)
                services.AddSingleton<IDialogService, DialogService>();
                services.AddTransient<HomeViewModel>();
                services.AddTransient<MediaDetailViewModel>();

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

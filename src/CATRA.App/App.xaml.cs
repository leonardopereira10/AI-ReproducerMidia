using System.Windows;
using CATRA.Core.Library;
using CATRA.Core.Navigation;
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

                // Navigation
                services.AddSingleton<FrameNavigationService>();
                services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<FrameNavigationService>());
            })
            .Build();

        await _host.StartAsync();

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

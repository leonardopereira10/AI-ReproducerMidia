using System.Net.Sockets;
using System.Windows;
using CATRA.Core.Interfaces;
using CATRA.UI.Navigation;
using CATRA.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CATRA.App;

/// <summary>
/// Shell window: custom title bar + content region (Frame with back stack).
/// Implements <see cref="IFullscreenHost"/> so the player (ST-06) can hide
/// the chrome in fullscreen.
/// </summary>
public partial class MainWindow : Window, IFullscreenHost
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var navigation = App.Services.GetRequiredService<FrameNavigationService>();
        navigation.Attach(ContentFrame);
        navigation.Navigate(typeof(HomeView));

        // ST-10: update web panel URL in the status bar once the server is ready.
        // The server starts fire-and-forget, so retry after a short delay if not ready yet.
        UpdateWebPanelUrl();
        if (App.Services.GetService<IWebControlServer>()?.IsRunning != true)
        {
            _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(UpdateWebPanelUrl));
        }
    }

    /// <summary>ST-10: shows the web control panel URL in the status bar.</summary>
    private void UpdateWebPanelUrl()
    {
        try
        {
            var server = App.Services.GetService<IWebControlServer>();
            if (server is not null && server.IsRunning)
            {
                var ip = GetLocalIpAddress();
                WebPanelUrlText.Text = $"📱 Painel: http://{ip}:{server.Port}";
                StatusBarBorder.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // Server not yet started or unavailable — status bar stays hidden.
        }
    }

    /// <summary>Gets the first IPv4 LAN address, falling back to localhost.</summary>
    private static string GetLocalIpAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch
        {
            // Fallback below.
        }

        return "localhost";
    }

    /// <summary>ST-10: copies the panel URL to the clipboard on click.</summary>
    private void WebPanelUrl_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var url = WebPanelUrlText.Text.Replace("📱 Painel: ", string.Empty);
            Clipboard.SetText(url);
            WebPanelUrlText.Text = "✅ URL copiada!";
            _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                var server = App.Services.GetService<IWebControlServer>();
                if (server is not null && server.IsRunning)
                {
                    var ip = GetLocalIpAddress();
                    WebPanelUrlText.Text = $"📱 Painel: http://{ip}:{server.Port}";
                }
            }));
        }
        catch
        {
            // Clipboard access can fail in rare cases — ignore.
        }
    }

    /// <inheritdoc />
    public void SetTitleBarVisible(bool visible)
    {
        TitleBarRow.Height = visible ? new GridLength(36) : new GridLength(0);
        TitleBarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}

using System.Windows;
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

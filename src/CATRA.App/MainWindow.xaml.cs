using System.Windows;
using CATRA.UI.Navigation;
using CATRA.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CATRA.App;

/// <summary>
/// Shell window: custom title bar + content region (Frame with back stack).
/// </summary>
public partial class MainWindow : Window
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
        navigation.Navigate(typeof(HomeDummyPage));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}

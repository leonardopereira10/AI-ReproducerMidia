using System.Windows;
using System.Windows.Controls;

namespace CATRA.UI.Views;

/// <summary>
/// Dummy page used to exercise the navigation skeleton.
/// </summary>
public partial class HomeDummyPage : Page
{
    public HomeDummyPage()
    {
        InitializeComponent();
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
        => NavigationService?.Navigate(new SettingsDummyPage());
}

using System.Windows;
using System.Windows.Controls;

namespace CATRA.UI.Views;

/// <summary>
/// Dummy page used to exercise the navigation skeleton.
/// </summary>
public partial class SettingsDummyPage : Page
{
    public SettingsDummyPage()
    {
        InitializeComponent();
    }

    private void GoBack_Click(object sender, RoutedEventArgs e)
    {
        if (NavigationService?.CanGoBack == true)
        {
            NavigationService.GoBack();
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Settings screen (Tela 6, ST-11). The view model is injected by the
/// navigation service and loads/persists settings autonomously (auto-save);
/// the code-behind only wires the <see cref="Page.DataContext"/> and routes
/// LostFocus events for the free-typing resolution/FPS fields.
/// </summary>
public partial class SettingsView : Page
{
    /// <summary>Creates the page with its view model.</summary>
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>
    /// Routes LostFocus from resolution/FPS TextBoxes to the appropriate
    /// commit command on the view model (clamp + restore text).
    /// </summary>
    private void OnResolutionFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        switch ((sender as FrameworkElement)?.Name)
        {
            case nameof(LocalWidthBox):  vm.CommitLocalWidth();  break;
            case nameof(LocalHeightBox): vm.CommitLocalHeight(); break;
            case nameof(LocalFpsBox):    vm.CommitLocalFps();    break;
            case nameof(DlnaWidthBox):   vm.CommitDlnaWidth();   break;
            case nameof(DlnaHeightBox):  vm.CommitDlnaHeight();  break;
            case nameof(DlnaFpsBox):     vm.CommitDlnaFps();     break;
        }
    }
}

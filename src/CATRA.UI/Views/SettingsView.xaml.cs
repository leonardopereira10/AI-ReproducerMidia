using System.Windows.Controls;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Settings screen (Tela 6, ST-11). The view model is injected by the
/// navigation service and loads/persists settings autonomously (auto-save);
/// the code-behind only wires the <see cref="Page.DataContext"/>.
/// </summary>
public partial class SettingsView : Page
{
    /// <summary>Creates the page with its view model.</summary>
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}

using System.Windows;
using System.Windows.Controls;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Home / library screen (Tela 1). The view model is injected by the
/// navigation service; data is (re)loaded every time the page is shown so the
/// grid reflects changes made on the detail screen.
/// </summary>
public partial class HomeView : Page
{
    private readonly HomeViewModel _viewModel;

    /// <summary>Creates the page with its view model.</summary>
    public HomeView(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Falha ao carregar biblioteca: {ex.Message}";
        }
    }
}

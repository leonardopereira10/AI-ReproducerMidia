using System.Windows.Controls;
using CATRA.Core.Navigation;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Media detail screen (Tela 2). Receives the media item id through
/// <see cref="INavigationAware"/> when the navigation service navigates here.
/// </summary>
public partial class MediaDetailView : Page, INavigationAware
{
    private readonly MediaDetailViewModel _viewModel;

    /// <summary>Creates the page with its view model.</summary>
    public MediaDetailView(MediaDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
    }

    /// <inheritdoc />
    public async void OnNavigatedTo(object? parameter)
    {
        if (parameter is int mediaItemId)
        {
            try
            {
                await _viewModel.LoadAsync(mediaItemId);
            }
            catch (Exception ex)
            {
                _viewModel.Title = $"Erro ao carregar: {ex.Message}";
            }
        }
    }
}

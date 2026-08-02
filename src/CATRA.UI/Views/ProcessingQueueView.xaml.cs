using System.Windows.Controls;
using CATRA.Core.Navigation;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Pre-processing queue screen (Tela 3, ST-19). Receives an optional media item
/// id through <see cref="INavigationAware"/>; when absent the view model falls
/// back to the active sliding window's series.
/// </summary>
public partial class ProcessingQueueView : Page, INavigationAware
{
    private readonly ProcessingQueueViewModel _viewModel;

    /// <summary>Creates the page with its view model.</summary>
    public ProcessingQueueView(ProcessingQueueViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
    }

    /// <inheritdoc />
    public async void OnNavigatedTo(object? parameter)
    {
        int? mediaItemId = parameter is int id ? id : null;
        try
        {
            await _viewModel.LoadAsync(mediaItemId);
        }
        catch (Exception ex)
        {
            _viewModel.SeriesTitle = $"Erro ao carregar: {ex.Message}";
        }
    }
}

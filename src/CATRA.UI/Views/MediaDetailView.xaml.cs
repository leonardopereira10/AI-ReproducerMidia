using System.Windows;
using System.Windows.Controls;
using CATRA.Core.Navigation;
using CATRA.UI.ViewModels;

namespace CATRA.UI.Views;

/// <summary>
/// Media detail screen (Tela 2). Receives the media item id through
/// <see cref="INavigationAware"/> when the navigation service navigates here.
/// The page also reloads whenever it re-enters the visual tree (back
/// navigation restores this kept-alive page without calling
/// <see cref="OnNavigatedTo"/>), same pattern as <see cref="HomeView"/>.
/// </summary>
public partial class MediaDetailView : Page, INavigationAware
{
    private readonly MediaDetailViewModel _viewModel;
    private int? _mediaItemId;
    private bool _initialShow;

    /// <summary>Creates the page with its view model.</summary>
    public MediaDetailView(MediaDetailViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <inheritdoc />
    public async void OnNavigatedTo(object? parameter)
    {
        if (parameter is int mediaItemId)
        {
            _mediaItemId = mediaItemId;
            await LoadSafeAsync();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialShow)
        {
            // First time in the tree: OnNavigatedTo performs the initial load.
            _initialShow = true;
            return;
        }

        // Back navigation (e.g. returning from the player) restores this
        // kept-alive page without calling OnNavigatedTo; reload so the
        // watched/progress changes persisted by the player are reflected.
        await LoadSafeAsync();
    }

    private async Task LoadSafeAsync()
    {
        if (_mediaItemId is not int mediaItemId)
        {
            return;
        }

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

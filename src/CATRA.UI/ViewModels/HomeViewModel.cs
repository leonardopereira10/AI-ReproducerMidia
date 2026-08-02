using System.Collections.ObjectModel;
using System.Windows;
using CATRA.Core.Library;
using CATRA.UI.Navigation;
using CATRA.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CATRA.UI.ViewModels;

/// <summary>
/// A category tab on the Home screen; <c>Id == null</c> is the "Todos" tab.
/// </summary>
/// <param name="Id">Category id, or <c>null</c> for "Todos".</param>
/// <param name="Name">Tab label.</param>
/// <param name="Count">Media items in the tab.</param>
public sealed record CategoryTab(int? Id, string Name, int Count);

/// <summary>
/// Home / library screen (Tela 1): category tabs, media card grid, manual
/// refresh (RF-01) and navigation to the detail screen.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _library;
    private readonly IAppNavigator _navigator;
    private readonly IDialogService _dialogs;

    private IReadOnlyList<MediaItemSummary> _allItems = [];
    private bool _isLoading;

    // Suppresses the event-driven reload while a manual refresh is already
    // reloading, avoiding a duplicate LoadAsync (RF-01).
    private bool _suppressEventReload;
    private bool _disposed;

    /// <summary>Creates the view model with its dependencies.</summary>
    public HomeViewModel(ILibraryService library, IAppNavigator navigator, IDialogService dialogs)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _library.LibraryUpdated += OnLibraryUpdated;
    }

    /// <summary>Category tabs ("Todos" first).</summary>
    public ObservableCollection<CategoryTab> Categories { get; } = [];

    /// <summary>Media cards currently displayed (category + search filtered).</summary>
    public ObservableCollection<MediaItemSummary> MediaItems { get; } = [];

    /// <summary>Currently selected category tab.</summary>
    [ObservableProperty]
    private CategoryTab? _selectedCategory;

    /// <summary>Search filter applied to card titles.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Whether a library refresh (scan) is running.</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>Bottom status line (scan result / errors).</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Whether at least one card is visible (empty-state toggle).</summary>
    [ObservableProperty]
    private bool _hasMediaItems;

    /// <summary>Loads categories and cards; safe to call on every activation.</summary>
    public async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            var categories = await _library.GetCategoriesAsync();
            var preservedId = SelectedCategory?.Id;

            Categories.Clear();
            Categories.Add(new CategoryTab(null, "Todos", categories.Sum(c => c.MediaItemCount)));
            foreach (var category in categories)
            {
                Categories.Add(new CategoryTab(category.Id, category.Name, category.MediaItemCount));
            }

            var target = Categories.FirstOrDefault(t => t.Id == preservedId) ?? Categories[0];
            _allItems = await _library.GetMediaItemsAsync(target.Id);
            SelectedCategory = target;
            ApplyFilter();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Runs an incremental scan and reloads (🔄 Atualizar).</summary>
    [RelayCommand]
    private async Task RefreshLibraryAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        StatusMessage = "Atualizando biblioteca...";
        try
        {
            // RefreshAsync raises LibraryUpdated; suppress the event-driven
            // reload so the explicit LoadAsync below is the only one that runs.
            _suppressEventReload = true;
            var summary = await _library.RefreshAsync();
            await LoadAsync();
            StatusMessage = summary.HasChanges
                ? $"Atualizado: +{summary.MediaItemsAdded} itens, +{summary.EpisodesAdded} eps, " +
                  $"-{summary.EpisodesRemoved} removidos ({summary.Elapsed.TotalSeconds:F1}s)"
                : "Biblioteca já está atualizada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao atualizar: {ex.Message}";
        }
        finally
        {
            _suppressEventReload = false;
            IsRefreshing = false;
        }
    }

    /// <summary>Navigates to the detail screen of the clicked card.</summary>
    [RelayCommand]
    private void OpenMedia(MediaItemSummary? media)
    {
        if (media is not null)
        {
            _navigator.GoToMediaDetail(media.Id);
        }
    }

    /// <summary>Settings placeholder (real settings screen is ST-11).</summary>
    [RelayCommand]
    private void OpenSettings() =>
        _dialogs.ShowMessage("Configurações", "A tela de configurações chega em ST-11.");

    async partial void OnSelectedCategoryChanged(CategoryTab? value)
    {
        if (_isLoading || value is null)
        {
            ApplyFilter();
            return;
        }

        _allItems = await _library.GetMediaItemsAsync(value.Id);
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void OnLibraryUpdated(object? sender, CATRA.Core.Models.LibraryScanSummary e)
    {
        // A manual refresh already reloads explicitly; skip the duplicate.
        if (_suppressEventReload)
        {
            return;
        }

        // The scanner may raise from a background thread; marshal to the UI thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = ReloadSafelyAsync();
        }
        else
        {
            _ = dispatcher.InvokeAsync(ReloadSafelyAsync);
        }
    }

    /// <summary>
    /// Fire-and-forget reload wrapper that never lets an exception escape
    /// unobserved (would otherwise risk crashing the process).
    /// </summary>
    private async Task ReloadSafelyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao recarregar biblioteca: {ex.Message}";
        }
    }

    /// <summary>
    /// Unsubscribes from the singleton <see cref="ILibraryService.LibraryUpdated"/>
    /// event so transient instances do not leak. Note: the VM is registered as
    /// transient in DI; whoever creates it should dispose it (the navigation
    /// service currently does not, so an instance stays alive until collected —
    /// disposing detaches the handler and prevents duplicate reloads).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _library.LibraryUpdated -= OnLibraryUpdated;
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = query.Length == 0
            ? _allItems
            : _allItems
                .Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        MediaItems.Clear();
        foreach (var item in filtered)
        {
            MediaItems.Add(item);
        }

        HasMediaItems = MediaItems.Count > 0;
    }
}

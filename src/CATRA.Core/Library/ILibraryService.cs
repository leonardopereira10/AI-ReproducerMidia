using CATRA.Core.Models;

namespace CATRA.Core.Library;

/// <summary>
/// Facade consumed by the UI layer: read models for the Home (Tela 1) and
/// Detail (Tela 2) screens, watched-state toggling (RF-07) and library
/// refresh orchestration (RF-01).
/// </summary>
public interface ILibraryService
{
    /// <summary>
    /// Raised after <see cref="RefreshAsync"/> completes (even when the scan
    /// found no changes). The UI uses it to reload its collections.
    /// </summary>
    event EventHandler<LibraryScanSummary>? LibraryUpdated;

    /// <summary>
    /// Returns every category with its media-item count, ordered by name.
    /// </summary>
    Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync();

    /// <summary>
    /// Returns the media item (series/movie) with the given id, or <c>null</c>.
    /// </summary>
    Task<MediaItem?> GetMediaItemAsync(int mediaItemId);

    /// <summary>
    /// Returns media cards for all items, or only the ones in
    /// <paramref name="categoryId"/>, ordered by title.
    /// </summary>
    Task<IReadOnlyList<MediaItemSummary>> GetMediaItemsAsync(int? categoryId = null);

    /// <summary>
    /// Returns the episodes of a media item joined with their watch states,
    /// ordered by episode number (then file name).
    /// </summary>
    Task<IReadOnlyList<EpisodeDetail>> GetEpisodesAsync(int mediaItemId);

    /// <summary>
    /// Flips the watched flag of an episode (RF-07 manual toggle). Creates the
    /// watch-state row on first use; marking watched sets progress to 100%.
    /// </summary>
    /// <exception cref="ArgumentException">Episode id not found.</exception>
    Task ToggleWatchedAsync(int episodeId);

    /// <summary>
    /// Renames the display title of an episode (RF-02 manual rename).
    /// </summary>
    /// <exception cref="ArgumentException">Episode id not found.</exception>
    Task RenameEpisodeAsync(int episodeId, string newDisplayTitle);

    /// <summary>
    /// Sets (or clears, when <paramref name="coverPath"/> is <c>null</c>) the
    /// manual cover image of a media item (RF-08).
    /// </summary>
    /// <exception cref="ArgumentException">Media item id not found.</exception>
    Task SetMediaCoverAsync(int mediaItemId, string? coverPath);

    /// <summary>
    /// Returns the media items with at least one "Continuar Assistindo"
    /// episode (RN-03), most recently active first.
    /// </summary>
    Task<IReadOnlyList<MediaItemSummary>> GetContinueWatchingAsync();

    /// <summary>
    /// Runs an incremental library scan (RF-01) and raises
    /// <see cref="LibraryUpdated"/> when it completes.
    /// </summary>
    Task<LibraryScanSummary> RefreshAsync(CancellationToken cancellationToken = default);
}

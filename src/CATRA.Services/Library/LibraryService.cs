using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// UI-facing facade over the library repositories and the incremental scanner
/// (RF-01, RF-07, RN-03). All reads are synchronous SQLite calls wrapped in
/// completed tasks; the only truly asynchronous operation is the scan.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    /// <summary>RN-03: "Continuar Assistindo" requires progress below this percentage.</summary>
    public const double ContinueWatchingMaxProgressPct = 85d;

    /// <summary>RN-03: "Continuar Assistindo" requires a position past this many seconds.</summary>
    public const double ContinueWatchingMinPositionSec = 30d;

    private readonly ICategoryRepository _categories;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IEpisodeRepository _episodes;
    private readonly IWatchStateRepository _watchStates;
    private readonly ILibraryScanner _scanner;

    /// <summary>Creates the facade with its dependencies.</summary>
    public LibraryService(
        ICategoryRepository categories,
        IMediaItemRepository mediaItems,
        IEpisodeRepository episodes,
        IWatchStateRepository watchStates,
        ILibraryScanner scanner)
    {
        _categories = categories ?? throw new ArgumentNullException(nameof(categories));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _watchStates = watchStates ?? throw new ArgumentNullException(nameof(watchStates));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    /// <inheritdoc />
    public event EventHandler<LibraryScanSummary>? LibraryUpdated;

    /// <inheritdoc />
    public Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync()
    {
        var summaries = _categories.GetAll()
            .Select(c => new CategorySummary(c, _mediaItems.GetByCategory(c.Id).Count))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<CategorySummary>>(summaries);
    }

    /// <inheritdoc />
    public Task<MediaItem?> GetMediaItemAsync(int mediaItemId) =>
        Task.FromResult(_mediaItems.GetById(mediaItemId));

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaItemSummary>> GetMediaItemsAsync(int? categoryId = null)
    {
        var items = categoryId is { } id
            ? _mediaItems.GetByCategory(id)
            : _mediaItems.GetAll();

        var summaries = items
            .Select(BuildSummary)
            .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<MediaItemSummary>>(summaries);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EpisodeDetail>> GetEpisodesAsync(int mediaItemId)
    {
        var details = _episodes.GetByMediaItem(mediaItemId)
            .Select(e => new EpisodeDetail(e, _watchStates.GetByEpisodeId(e.Id)))
            .OrderBy(d => d.Episode.EpisodeNumber ?? int.MaxValue)
            .ThenBy(d => d.Episode.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<EpisodeDetail>>(details);
    }

    /// <inheritdoc />
    public Task ToggleWatchedAsync(int episodeId)
    {
        if (_episodes.GetById(episodeId) is null)
        {
            throw new ArgumentException($"Episode {episodeId} not found.", nameof(episodeId));
        }

        var state = _watchStates.GetByEpisodeId(episodeId);
        if (state is null)
        {
            _watchStates.Insert(new WatchState
            {
                EpisodeId = episodeId,
                Watched = true,
                ProgressPct = 100d,
                LastPositionSec = 0d,
            });
        }
        else
        {
            state.Watched = !state.Watched;
            if (state.Watched)
            {
                state.ProgressPct = 100d;
            }

            _watchStates.Update(state);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RenameEpisodeAsync(int episodeId, string newDisplayTitle)
    {
        var episode = _episodes.GetById(episodeId)
            ?? throw new ArgumentException($"Episode {episodeId} not found.", nameof(episodeId));

        episode.DisplayTitle = newDisplayTitle?.Trim() ?? string.Empty;
        _episodes.Update(episode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetMediaCoverAsync(int mediaItemId, string? coverPath)
    {
        var item = _mediaItems.GetById(mediaItemId)
            ?? throw new ArgumentException($"Media item {mediaItemId} not found.", nameof(mediaItemId));

        item.CoverPath = string.IsNullOrWhiteSpace(coverPath) ? null : coverPath.Trim();
        _mediaItems.Update(item);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaItemSummary>> GetContinueWatchingAsync()
    {
        var summaries = _mediaItems.GetAll()
            .Select(BuildSummary)
            .Where(s => s.HasContinueWatching)
            .OrderByDescending(s => s.LastActivityUtc ?? DateTime.MinValue)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<MediaItemSummary>>(summaries);
    }

    /// <inheritdoc />
    public async Task<LibraryScanSummary> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var summary = await _scanner
            .ScanAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        LibraryUpdated?.Invoke(this, summary);
        return summary;
    }

    private MediaItemSummary BuildSummary(MediaItem item)
    {
        var episodeIds = _episodes.GetByMediaItem(item.Id);
        var states = episodeIds
            .Select(e => _watchStates.GetByEpisodeId(e.Id))
            .Where(s => s is not null)
            .Cast<WatchState>()
            .ToList();

        var watchedCount = states.Count(s => s.Watched);
        var hasContinueWatching = states.Any(IsContinueWatching);
        DateTime? lastActivity = states.Count == 0
            ? null
            : states.Max(s => s.UpdatedAt);

        return new MediaItemSummary(item, episodeIds.Count, watchedCount, hasContinueWatching, lastActivity);
    }

    private static bool IsContinueWatching(WatchState state) =>
        !state.Watched &&
        state.ProgressPct > 0d &&
        state.ProgressPct < ContinueWatchingMaxProgressPct &&
        state.LastPositionSec > ContinueWatchingMinPositionSec;
}

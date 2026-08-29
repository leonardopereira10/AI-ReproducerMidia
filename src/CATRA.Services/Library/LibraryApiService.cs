using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// Implementation of <see cref="ILibraryApiService"/> that maps domain entities
/// from the repository layer to immutable DTOs for the presentation layer.
/// </summary>
public sealed class LibraryApiService : ILibraryApiService
{
    private readonly ICategoryRepository _categories;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IEpisodeRepository _episodes;
    private readonly IWatchStateRepository _watchStates;
    private readonly IProcessedFileRepository _processedFiles;

    public LibraryApiService(
        ICategoryRepository categories,
        IMediaItemRepository mediaItems,
        IEpisodeRepository episodes,
        IWatchStateRepository watchStates,
        IProcessedFileRepository processedFiles)
    {
        _categories = categories;
        _mediaItems = mediaItems;
        _episodes = episodes;
        _watchStates = watchStates;
        _processedFiles = processedFiles;
    }

    /// <inheritdoc />
    public Task<List<CategoryDto>> GetCategoriesAsync()
    {
        var all = _categories.GetAll();
        var dtos = all
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CategoryDto(c.Id, c.Name, c.FolderPath))
            .ToList();

        return Task.FromResult(dtos);
    }

    /// <inheritdoc />
    public Task<List<MediaItemDto>> GetItemsByCategoryAsync(int categoryId)
    {
        var items = _mediaItems.GetByCategory(categoryId);
        var dtos = items
            .Select(m => MapMediaItem(m))
            .ToList();

        return Task.FromResult(dtos);
    }

    /// <inheritdoc />
    public Task<List<EpisodeDto>> GetEpisodesByItemAsync(int mediaItemId)
    {
        var episodes = _episodes.GetByMediaItem(mediaItemId);
        var dtos = episodes
            .Select(e => MapEpisode(e))
            .ToList();

        return Task.FromResult(dtos);
    }

    /// <inheritdoc />
    public Task<List<SearchResultDto>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new List<SearchResultDto>());

        var trimmed = query.Trim();

        // Search MediaItems by title (case-insensitive)
        var allItems = _mediaItems.GetAll();
        var matchedItems = allItems
            .Where(m => m.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(m => new SearchResultDto(
                m.Id,
                m.CategoryId,
                m.Title,
                m.MediaType,
                m.CoverPath,
                m.Year,
                m.Genre,
                m.PosterUrl))
            .ToList();

        // Search Episodes by DisplayTitle or FileName (case-insensitive)
        // Need to join with parent MediaItem for CategoryId etc.
        var allEpisodes = _episodes.GetAll();
        var matchedEpisodes = new List<SearchResultDto>();
        foreach (var e in allEpisodes)
        {
            bool titleMatch = e.DisplayTitle != null &&
                              e.DisplayTitle.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
            bool fileMatch = e.FileName.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
            if (!titleMatch && !fileMatch) continue;

            var parent = _mediaItems.GetById(e.MediaItemId);
            if (parent is null) continue;

            matchedEpisodes.Add(new SearchResultDto(
                parent.Id,
                parent.CategoryId,
                parent.Title,
                parent.MediaType,
                parent.CoverPath,
                parent.Year,
                parent.Genre,
                parent.PosterUrl));
        }

        // Merge and deduplicate by Id (MediaItem id)
        var results = matchedItems
            .Concat(matchedEpisodes)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();

        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<List<ContinueWatchingDto>> GetContinueWatchingAsync()
    {
        var inProgress = _watchStates.GetInProgress();

        var dtos = new List<ContinueWatchingDto>();
        foreach (var w in inProgress
                     .Where(w => w.ProgressPct > 0 && w.ProgressPct < 100)
                     .OrderByDescending(w => w.UpdatedAt)
                     .Take(20))
        {
            var episode = _episodes.GetById(w.EpisodeId);
            if (episode is null) continue;

            var mediaItem = _mediaItems.GetById(episode.MediaItemId);
            if (mediaItem is null) continue;

            dtos.Add(new ContinueWatchingDto(
                w.EpisodeId,
                mediaItem.Id,
                mediaItem.Title,
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.DisplayTitle,
                w.ProgressPct,
                w.LastPositionSec,
                episode.DurationSec,
                episode.ThumbnailPath));
        }

        return Task.FromResult(dtos);
    }

    #region Mapping helpers

    private static MediaItemDto MapMediaItem(MediaItem m) =>
        new(m.Id, m.CategoryId, m.Title, m.MediaType, m.CoverPath, m.Synopsis, m.Year, m.Genre, m.PosterUrl);

    private static EpisodeDto MapEpisode(Episode e) =>
        new(e.Id, e.MediaItemId, e.FilePath, e.SeasonNumber, e.EpisodeNumber, e.DisplayTitle, e.DurationSec, e.ThumbnailPath);

    #endregion
}

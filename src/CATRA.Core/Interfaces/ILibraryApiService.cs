using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Public-facing library API surface used by the presentation layer to query
/// the media library. Implementations map domain entities to immutable DTOs.
/// </summary>
public interface ILibraryApiService
{
    /// <summary>Returns all library categories ordered by name.</summary>
    Task<List<CategoryDto>> GetCategoriesAsync();

    /// <summary>Returns the media items belonging to a category.</summary>
    Task<List<MediaItemDto>> GetItemsByCategoryAsync(int categoryId);

    /// <summary>Returns the episodes of a media item.</summary>
    Task<List<EpisodeDto>> GetEpisodesByItemAsync(int mediaItemId);

    /// <summary>Searches the library for media items matching <paramref name="query"/>.</summary>
    Task<List<SearchResultDto>> SearchAsync(string query);

    /// <summary>Returns episodes with playback in progress.</summary>
    Task<List<ContinueWatchingDto>> GetContinueWatchingAsync();
}
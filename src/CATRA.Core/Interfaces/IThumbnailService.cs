using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Thumbnail and cover management (RF-08, RN-05). Extracts a representative
/// frame from each video into a local cache, derives series covers from the
/// first episode, supports a manual cover override and falls back to
/// <c>null</c> (UI placeholder) whenever extraction is impossible.
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// Returns the cached thumbnail path for an episode, extracting it on first
    /// use. Returns <c>null</c> when extraction fails (UI shows a placeholder).
    /// </summary>
    Task<string?> GetOrCreateThumbnailAsync(Episode episode);

    /// <summary>
    /// Returns the cover path for a media item. Priority: manual custom cover
    /// &gt; cached auto cover &gt; freshly extracted first-episode frame &gt;
    /// <c>null</c> (placeholder).
    /// </summary>
    Task<string?> GetOrCreateSeriesCoverAsync(MediaItem mediaItem);

    /// <summary>
    /// Copies a user-supplied image into the cache as the media item's custom
    /// cover and persists it to <see cref="MediaItem.CoverPath"/>. Custom
    /// covers always win over auto-extracted ones.
    /// </summary>
    Task SetCustomCoverAsync(int mediaItemId, string imagePath);

    /// <summary>Deletes every cached thumbnail and cover file.</summary>
    Task ClearCacheAsync();
}

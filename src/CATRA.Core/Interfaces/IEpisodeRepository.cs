using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for <see cref="Episode"/> domain models.
/// </summary>
public interface IEpisodeRepository : IRepository<Episode>
{
    /// <summary>Returns all episodes of a media item.</summary>
    IReadOnlyList<Episode> GetByMediaItem(int mediaItemId);

    /// <summary>
    /// Returns the unwatched episodes of a media item ordered by
    /// <see cref="Episode.EpisodeNumber"/> ascending.
    /// </summary>
    IReadOnlyList<Episode> GetUnwatchedByMediaItem(int mediaItemId);
}

using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for <see cref="WatchState"/> domain models.
/// </summary>
public interface IWatchStateRepository : IRepository<WatchState>
{
    /// <summary>Returns the watch state for an episode, or <c>null</c>.</summary>
    WatchState? GetByEpisodeId(int episodeId);

    /// <summary>Returns states with progress started but not yet watched.</summary>
    IReadOnlyList<WatchState> GetInProgress();
}

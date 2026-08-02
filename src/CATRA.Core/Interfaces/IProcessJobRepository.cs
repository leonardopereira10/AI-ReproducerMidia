using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for <see cref="ProcessJob"/> domain models.
/// </summary>
public interface IProcessJobRepository : IRepository<ProcessJob>
{
    /// <summary>
    /// Returns the active (queued or processing) jobs for a media item's episodes,
    /// ordered by priority descending then creation ascending.
    /// </summary>
    IReadOnlyList<ProcessJob> GetActiveByMediaItem(int mediaItemId);
}

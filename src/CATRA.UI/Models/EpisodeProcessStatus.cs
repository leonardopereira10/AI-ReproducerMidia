using CATRA.Core.Enums;
using CATRA.Core.Models;

namespace CATRA.UI.Models;

/// <summary>
/// Pure mapping from (episode, processed file, job) to an
/// <see cref="EpisodeProcessStatus"/>. Kept static and side-effect free so the
/// badge logic is unit-testable without any UI or database.
/// </summary>
public static class EpisodeProcessStatusMapper
{
    /// <summary>
    /// Computes the badge state. <paramref name="processed"/> and
    /// <paramref name="job"/> are the rows for the episode under the active
    /// profile (both may be <c>null</c>).
    /// </summary>
    /// <remarks>
    /// Precedence: stale (hash changed) &gt; processing &gt; queued &gt; ready &gt;
    /// original. A stale file wins over "ready" so the user is prompted to
    /// reprocess; an active job wins over a ready file so the badge reflects
    /// the in-flight reprocessing.
    /// </remarks>
    public static EpisodeProcessStatus Compute(
        Episode episode,
        ProcessedFile? processed,
        ProcessJob? job)
    {
        ArgumentNullException.ThrowIfNull(episode);

        bool stale = processed is not null
            && !string.IsNullOrEmpty(episode.FileHash)
            && !string.Equals(processed.SourceHash, episode.FileHash, StringComparison.Ordinal);
        if (stale)
        {
            return EpisodeProcessStatus.Stale;
        }

        if (job is not null)
        {
            if (job.Status == JobStatus.Processing)
            {
                return EpisodeProcessStatus.Processing;
            }

            if (job.Status == JobStatus.Queued)
            {
                return EpisodeProcessStatus.Queued;
            }
        }

        if (processed is not null)
        {
            return EpisodeProcessStatus.Ready;
        }

        return EpisodeProcessStatus.Original;
    }
}

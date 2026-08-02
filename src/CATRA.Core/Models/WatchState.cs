namespace CATRA.Core.Models;

/// <summary>
/// Domain model for per-episode playback / watched state.
/// </summary>
public sealed class WatchState
{
    /// <summary>Database identity. Zero when not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Associated episode id (unique).</summary>
    public int EpisodeId { get; set; }

    /// <summary>Whether the episode is marked watched.</summary>
    public bool Watched { get; set; }

    /// <summary>Watched progress percentage (0-100).</summary>
    public double ProgressPct { get; set; }

    /// <summary>Last playback position in seconds (in the ORIGINAL file).</summary>
    public double LastPositionSec { get; set; }

    /// <summary>Last update timestamp (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

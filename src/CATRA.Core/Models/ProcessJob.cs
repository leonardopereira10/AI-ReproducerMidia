using CATRA.Core.Enums;

namespace CATRA.Core.Models;

/// <summary>
/// Domain model for a queued/running processing job.
/// </summary>
public sealed class ProcessJob
{
    /// <summary>Database identity. Zero when not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Target episode id.</summary>
    public int EpisodeId { get; set; }

    /// <summary>Processing profile.</summary>
    public ProcessProfile Profile { get; set; }

    /// <summary>Job lifecycle status.</summary>
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Scheduling priority (higher first).</summary>
    public int Priority { get; set; }

    /// <summary>Completion percentage (0-100).</summary>
    public double ProgressPct { get; set; }

    /// <summary>Current pipeline step, if running.</summary>
    public ProcessStep? CurrentStep { get; set; }

    /// <summary>Error message when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>When processing started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When processing finished.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

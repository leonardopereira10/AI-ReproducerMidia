using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>ProcessJob</c> table.
/// </summary>
[Table("ProcessJob")]
public sealed class ProcessJobEntity
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, AutoIncrement, Column("Id")]
    public int Id { get; set; }

    /// <summary>Target episode id.</summary>
    [NotNull, Indexed("IX_ProcessJob_Episode_Profile", 1, Unique = true), Column("EpisodeId")]
    public int EpisodeId { get; set; }

    /// <summary>Profile as text (<c>local</c> / <c>dlna</c>).</summary>
    [NotNull, Indexed("IX_ProcessJob_Episode_Profile", 2, Unique = true), Column("Profile")]
    public string Profile { get; set; } = string.Empty;

    /// <summary>Status as text.</summary>
    [NotNull, Column("Status")]
    public string Status { get; set; } = "queued";

    /// <summary>Priority.</summary>
    [NotNull, Column("Priority")]
    public int Priority { get; set; }

    /// <summary>Progress percentage.</summary>
    [NotNull, Column("ProgressPct")]
    public double ProgressPct { get; set; }

    /// <summary>Current step as text.</summary>
    [Column("CurrentStep")]
    public string? CurrentStep { get; set; }

    /// <summary>Error message.</summary>
    [Column("ErrorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Start timestamp.</summary>
    [Column("StartedAt")]
    public DateTime? StartedAt { get; set; }

    /// <summary>Completion timestamp.</summary>
    [Column("CompletedAt")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    [NotNull, Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

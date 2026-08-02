namespace CATRA.Core.Models;

/// <summary>
/// Domain model for a single video file (episode or movie file).
/// </summary>
public sealed class Episode
{
    /// <summary>Database identity. Zero when not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Owning media item id.</summary>
    public int MediaItemId { get; set; }

    /// <summary>File name with extension.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Absolute file path (unique).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Season number (default 1).</summary>
    public int SeasonNumber { get; set; } = 1;

    /// <summary>Episode number, if detectable.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Human-friendly title.</summary>
    public string? DisplayTitle { get; set; }

    /// <summary>Duration in seconds.</summary>
    public double? DurationSec { get; set; }

    /// <summary>File size in bytes.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Source frames-per-second.</summary>
    public double? SourceFps { get; set; }

    /// <summary>Source width in pixels.</summary>
    public int? SourceWidth { get; set; }

    /// <summary>Source height in pixels.</summary>
    public int? SourceHeight { get; set; }

    /// <summary>SHA-256 hash used to detect source changes.</summary>
    public string? FileHash { get; set; }

    /// <summary>Thumbnail image path.</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

using CATRA.Core.Enums;

namespace CATRA.Core.Models;

/// <summary>
/// Domain model for a processed output file (one per episode per profile).
/// </summary>
public sealed class ProcessedFile
{
    /// <summary>Database identity. Zero when not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Source episode id.</summary>
    public int EpisodeId { get; set; }

    /// <summary>Processing profile.</summary>
    public ProcessProfile Profile { get; set; }

    /// <summary>Path of the processed file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Processed file size in bytes.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Target frames-per-second.</summary>
    public double TargetFps { get; set; }

    /// <summary>Target width in pixels.</summary>
    public int TargetWidth { get; set; }

    /// <summary>Target height in pixels.</summary>
    public int TargetHeight { get; set; }

    /// <summary>Encode bitrate in kbps.</summary>
    public int? EncodeBitrate { get; set; }

    /// <summary>Interpolation method (<c>rife</c> / <c>fsr3fg</c> / <c>none</c>).</summary>
    public string? InterpMethod { get; set; }

    /// <summary>Upscale method (<c>fsr4</c> / <c>fsr1</c> / <c>none</c>).</summary>
    public string? UpscaleMethod { get; set; }

    /// <summary>Processing timestamp (UTC).</summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Hash of the original file used.</summary>
    public string SourceHash { get; set; } = string.Empty;
}

using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>ProcessedFile</c> table.
/// </summary>
[Table("ProcessedFile")]
public sealed class ProcessedFileEntity
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, AutoIncrement, Column("Id")]
    public int Id { get; set; }

    /// <summary>Source episode id.</summary>
    [NotNull, Indexed("IX_ProcessedFile_Episode_Profile", 1, Unique = true), Column("EpisodeId")]
    public int EpisodeId { get; set; }

    /// <summary>Profile as text (<c>local</c> / <c>dlna</c>).</summary>
    [NotNull, Indexed("IX_ProcessedFile_Episode_Profile", 2, Unique = true), Column("Profile")]
    public string Profile { get; set; } = string.Empty;

    /// <summary>Processed file path.</summary>
    [NotNull, Column("FilePath")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>File size bytes.</summary>
    [Column("FileSizeBytes")]
    public long? FileSizeBytes { get; set; }

    /// <summary>Target fps.</summary>
    [NotNull, Column("TargetFps")]
    public double TargetFps { get; set; }

    /// <summary>Target width.</summary>
    [NotNull, Column("TargetWidth")]
    public int TargetWidth { get; set; }

    /// <summary>Target height.</summary>
    [NotNull, Column("TargetHeight")]
    public int TargetHeight { get; set; }

    /// <summary>Encode bitrate kbps.</summary>
    [Column("EncodeBitrate")]
    public int? EncodeBitrate { get; set; }

    /// <summary>Interpolation method.</summary>
    [Column("InterpMethod")]
    public string? InterpMethod { get; set; }

    /// <summary>Upscale method.</summary>
    [Column("UpscaleMethod")]
    public string? UpscaleMethod { get; set; }

    /// <summary>Processing timestamp (UTC).</summary>
    [NotNull, Column("ProcessedAt")]
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Hash of the original file.</summary>
    [NotNull, Column("SourceHash")]
    public string SourceHash { get; set; } = string.Empty;
}

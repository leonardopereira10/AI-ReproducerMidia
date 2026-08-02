using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>Episode</c> table.
/// </summary>
[Table("Episode")]
public sealed class EpisodeEntity
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, AutoIncrement, Column("Id")]
    public int Id { get; set; }

    /// <summary>Owning media item id.</summary>
    [NotNull, Indexed, Column("MediaItemId")]
    public int MediaItemId { get; set; }

    /// <summary>File name.</summary>
    [NotNull, Column("FileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Absolute file path (unique).</summary>
    [NotNull, Unique, Column("FilePath")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Season number.</summary>
    [Column("SeasonNumber")]
    public int SeasonNumber { get; set; } = 1;

    /// <summary>Episode number.</summary>
    [Column("EpisodeNumber")]
    public int? EpisodeNumber { get; set; }

    /// <summary>Display title.</summary>
    [Column("DisplayTitle")]
    public string? DisplayTitle { get; set; }

    /// <summary>Duration seconds.</summary>
    [Column("DurationSec")]
    public double? DurationSec { get; set; }

    /// <summary>File size bytes.</summary>
    [Column("FileSizeBytes")]
    public long? FileSizeBytes { get; set; }

    /// <summary>Source fps.</summary>
    [Column("SourceFps")]
    public double? SourceFps { get; set; }

    /// <summary>Source width.</summary>
    [Column("SourceWidth")]
    public int? SourceWidth { get; set; }

    /// <summary>Source height.</summary>
    [Column("SourceHeight")]
    public int? SourceHeight { get; set; }

    /// <summary>Source SHA-256 hash.</summary>
    [Column("FileHash")]
    public string? FileHash { get; set; }

    /// <summary>Thumbnail path.</summary>
    [Column("ThumbnailPath")]
    public string? ThumbnailPath { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    [NotNull, Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

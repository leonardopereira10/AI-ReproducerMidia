using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>MediaItem</c> table.
/// </summary>
[Table("MediaItem")]
public sealed class MediaItemEntity
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, AutoIncrement, Column("Id")]
    public int Id { get; set; }

    /// <summary>Owning category id.</summary>
    [NotNull, Indexed, Column("CategoryId")]
    public int CategoryId { get; set; }

    /// <summary>Display title.</summary>
    [NotNull, Column("Title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Raw folder name.</summary>
    [NotNull, Column("RawFolderName")]
    public string RawFolderName { get; set; } = string.Empty;

    /// <summary>Folder path.</summary>
    [NotNull, Column("FolderPath")]
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Media type as text (<c>series</c> / <c>movie</c>).</summary>
    [NotNull, Column("MediaType")]
    public string MediaType { get; set; } = "series";

    /// <summary>Intro skip seconds.</summary>
    [NotNull, Column("SkipIntroSec")]
    public double SkipIntroSec { get; set; } = 85.0;

    /// <summary>Cover path.</summary>
    [Column("CoverPath")]
    public string? CoverPath { get; set; }

    /// <summary>TMDB id.</summary>
    [Column("TmdbId")]
    public int? TmdbId { get; set; }

    /// <summary>Synopsis.</summary>
    [Column("Synopsis")]
    public string? Synopsis { get; set; }

    /// <summary>Release year.</summary>
    [Column("Year")]
    public int? Year { get; set; }

    /// <summary>Genre.</summary>
    [Column("Genre")]
    public string? Genre { get; set; }

    /// <summary>Poster URL.</summary>
    [Column("PosterUrl")]
    public string? PosterUrl { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    [NotNull, Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last update timestamp (UTC).</summary>
    [NotNull, Column("UpdatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

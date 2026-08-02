using CATRA.Core.Enums;

namespace CATRA.Core.Models;

/// <summary>
/// Domain model for a series or movie (level 2 of the folder tree).
/// </summary>
public sealed class MediaItem
{
    /// <summary>Database identity. Zero when not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Owning category id.</summary>
    public int CategoryId { get; set; }

    /// <summary>Display title (normalized).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Original folder name on disk.</summary>
    public string RawFolderName { get; set; } = string.Empty;

    /// <summary>Absolute folder path.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Series or movie.</summary>
    public MediaType MediaType { get; set; } = MediaType.Series;

    /// <summary>Intro skip length in seconds.</summary>
    public double SkipIntroSec { get; set; } = 85.0;

    /// <summary>Optional manual cover image path.</summary>
    public string? CoverPath { get; set; }

    // Online metadata (Phase 3, nullable).

    /// <summary>TMDB identifier.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Synopsis.</summary>
    public string? Synopsis { get; set; }

    /// <summary>Release year.</summary>
    public int? Year { get; set; }

    /// <summary>Genre.</summary>
    public string? Genre { get; set; }

    /// <summary>Poster URL.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last update timestamp (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

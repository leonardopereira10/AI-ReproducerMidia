using CATRA.Core.Enums;

namespace CATRA.Core.Models;

/// <summary>
/// Immutable result of parsing a video file name (RF-02). Lives in Core because
/// <see cref="Interfaces.IFilenameParser"/> is a Core contract and layering forbids
/// Core from referencing Services.
/// </summary>
public sealed record FilenameParserResult
{
    /// <summary>Raw name (without extension) that was parsed.</summary>
    public string RawName { get; init; } = string.Empty;

    /// <summary>Normalized series name for P1/P2; <c>null</c> for P3/fallback.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Season number (P3 only); <c>null</c> otherwise.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Episode number (P1/P2/P3); <c>null</c> for fallback.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Release group / fansub / publisher tag (e.g. <c>DonghuaNoSekai</c>),
    /// captured by <see cref="FilenamePattern.PublisherRelease"/>; <c>null</c> otherwise.
    /// Kept verbatim (not title-cased) to preserve the group's original casing.
    /// </summary>
    public string? Publisher { get; init; }

    /// <summary>Which pattern matched.</summary>
    public FilenamePattern PatternUsed { get; init; }
}

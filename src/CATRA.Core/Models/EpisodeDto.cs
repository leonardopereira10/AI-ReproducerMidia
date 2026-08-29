namespace CATRA.Core.Models;

/// <summary>
/// Immutable representation of a single video file for the public API layer.
/// Maps from <see cref="Episode"/>.
/// </summary>
public sealed record EpisodeDto(
    int Id,
    int MediaItemId,
    string FilePath,
    int SeasonNumber,
    int? EpisodeNumber,
    string? DisplayTitle,
    double? DurationSec,
    string? ThumbnailPath);
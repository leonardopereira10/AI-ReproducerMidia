namespace CATRA.Core.Models;

/// <summary>
/// Immutable representation of an in-progress episode for the "continue watching"
/// API surface. Maps from <see cref="WatchState"/> joined with
/// <see cref="Episode"/> and <see cref="MediaItem"/>.
/// </summary>
public sealed record ContinueWatchingDto(
    int EpisodeId,
    int MediaItemId,
    string ItemTitle,
    int SeasonNumber,
    int? EpisodeNumber,
    string? DisplayTitle,
    double ProgressPct,
    double LastPositionSec,
    double? DurationSec,
    string? ThumbnailPath);
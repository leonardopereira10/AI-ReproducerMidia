using CATRA.Core.Enums;
using CATRA.Core.Models;

namespace CATRA.Core.Library;

/// <summary>
/// Read model for a media card on the Home screen (Tela 1): the media item
/// plus aggregated episode/watch progress.
/// </summary>
public sealed class MediaItemSummary
{
    /// <summary>Creates a summary from a media item and its aggregate counters.</summary>
    public MediaItemSummary(
        MediaItem item,
        int episodeCount,
        int watchedCount,
        bool hasContinueWatching,
        DateTime? lastActivityUtc)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        EpisodeCount = episodeCount;
        WatchedCount = watchedCount;
        HasContinueWatching = hasContinueWatching;
        LastActivityUtc = lastActivityUtc;
    }

    /// <summary>Underlying media item.</summary>
    public MediaItem Item { get; }

    /// <summary>Media item database id.</summary>
    public int Id => Item.Id;

    /// <summary>Normalized display title.</summary>
    public string Title => Item.Title;

    /// <summary>Series or movie.</summary>
    public MediaType MediaType => Item.MediaType;

    /// <summary>Total number of episodes (1 for movies).</summary>
    public int EpisodeCount { get; }

    /// <summary>Number of episodes marked watched.</summary>
    public int WatchedCount { get; }

    /// <summary>
    /// Whether at least one episode qualifies for the "Continuar Assistindo"
    /// badge (RN-03: progress &lt; 85%, position &gt; 30s, not watched).
    /// </summary>
    public bool HasContinueWatching { get; }

    /// <summary>Most recent watch-state update among this item's episodes (UTC).</summary>
    public DateTime? LastActivityUtc { get; }

    /// <summary>Every episode of this item is marked watched.</summary>
    public bool IsFullyWatched => EpisodeCount > 0 && WatchedCount >= EpisodeCount;

    /// <summary>Short type label for the card ("Filme" / "12 eps").</summary>
    public string TypeDisplay =>
        MediaType == MediaType.Movie ? "Filme" : EpisodeCount == 1 ? "1 ep" : $"{EpisodeCount} eps";

    /// <summary>
    /// Progress label for the card: "✓" when fully watched, "▶ 3/12" while in
    /// progress, "▶" when only a continue-watching episode exists, else empty.
    /// </summary>
    public string ProgressDisplay
    {
        get
        {
            if (EpisodeCount == 0)
            {
                return string.Empty;
            }

            if (IsFullyWatched)
            {
                return "✓";
            }

            if (MediaType == MediaType.Movie)
            {
                return HasContinueWatching || WatchedCount > 0 ? "▶" : string.Empty;
            }

            if (WatchedCount > 0)
            {
                return $"▶ {WatchedCount}/{EpisodeCount}";
            }

            return HasContinueWatching ? "▶" : string.Empty;
        }
    }
}

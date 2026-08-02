using CATRA.Core.Models;

namespace CATRA.Core.Library;

/// <summary>
/// Read model for an episode card on the Detail screen (Tela 2): the episode
/// joined with its (optional) watch state.
/// </summary>
public sealed class EpisodeDetail
{
    /// <summary>Creates a detail from an episode and its optional watch state.</summary>
    public EpisodeDetail(Episode episode, WatchState? watchState)
    {
        Episode = episode ?? throw new ArgumentNullException(nameof(episode));
        WatchState = watchState;
    }

    /// <summary>Underlying episode.</summary>
    public Episode Episode { get; }

    /// <summary>Watch state, or <c>null</c> when never played.</summary>
    public WatchState? WatchState { get; }

    /// <summary>Episode database id.</summary>
    public int Id => Episode.Id;

    /// <summary>Whether the episode is marked watched.</summary>
    public bool IsWatched => WatchState?.Watched == true;

    /// <summary>Watched progress percentage (0–100); 0 when never played.</summary>
    public double ProgressPct => WatchState?.ProgressPct ?? 0d;

    /// <summary>
    /// Whether the episode has playback progress but is not watched yet
    /// (candidate for "continue watching", RN-03).
    /// </summary>
    public bool HasProgress => !IsWatched && ProgressPct > 0d;

    /// <summary>Zero-padded episode label ("EP01"); falls back to "EP??".</summary>
    public string EpisodeLabel =>
        Episode.EpisodeNumber is { } number ? $"EP{number:D2}" : "EP??";

    /// <summary>Display title; falls back to the raw file name.</summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Episode.DisplayTitle) ? Episode.FileName : Episode.DisplayTitle;
}

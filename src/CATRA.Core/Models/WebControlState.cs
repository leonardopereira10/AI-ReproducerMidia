namespace CATRA.Core.Models;

/// <summary>
/// Snapshot of the player state exposed to the web control panel (ST-10).
/// Sent as a full JSON payload over WebSocket whenever the state changes.
/// </summary>
/// <param name="IsPlaying">Whether the player is currently playing.</param>
/// <param name="IsPaused">Whether the player is paused.</param>
/// <param name="Position">Current playback position in seconds.</param>
/// <param name="Duration">Total media duration in seconds.</param>
/// <param name="Volume">Current volume level (0–100).</param>
/// <param name="Title">Title of the currently playing media.</param>
/// <param name="ThumbnailUrl">Optional URL of the current media thumbnail.</param>
/// <param name="SkipIntroSec">Intro-skip endpoint in seconds (0 when no intro detected).</param>
/// <param name="CanSkipIntro">Whether the intro-skip action is currently available.</param>
/// <param name="HasNextEpisode">Whether a next episode is available in the series.</param>
/// <param name="HasPreviousEpisode">Whether a previous episode is available in the series.</param>
/// <param name="CastDeviceName">Name of the active cast device, or <c>null</c> when not casting.</param>
/// <param name="ProfileLabel">Label of the active playback profile, or <c>null</c> when using defaults.</param>
/// <param name="Queue">Current playback queue.</param>
public sealed record WebControlState(
    bool IsPlaying,
    bool IsPaused,
    double Position,
    double Duration,
    int Volume,
    string Title,
    string? ThumbnailUrl,
    double SkipIntroSec,
    bool CanSkipIntro,
    bool HasNextEpisode,
    bool HasPreviousEpisode,
    string? CastDeviceName,
    string? ProfileLabel,
    List<WebControlQueueItem> Queue);

/// <summary>
/// Single item in the web control playback queue (ST-10).
/// </summary>
/// <param name="Id">Unique identifier for the queue entry.</param>
/// <param name="Title">Display title of the queued item.</param>
/// <param name="DurationSec">Duration in seconds.</param>
/// <param name="IsCurrent">Whether this item is the one currently playing.</param>
public sealed record WebControlQueueItem(
    int Id,
    string Title,
    double DurationSec,
    bool IsCurrent);

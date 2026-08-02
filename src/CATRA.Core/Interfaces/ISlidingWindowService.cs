using CATRA.Core.Enums;
using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Manages the sliding pre-processing window (ST-18, RF-03, RN-10): keeps the next
/// <c>window_size</c> (default 5) unwatched episodes of a single series queued for
/// GPU pre-processing under one profile. When an episode is watched, its processed
/// file is reclaimed and the next unwatched episode slides into the window, keeping
/// the window full. Only one series has an active window at a time — starting a
/// window for a new series cancels the previous one.
/// </summary>
public interface ISlidingWindowService
{
    /// <summary>
    /// Opens the window for <paramref name="mediaItemId"/> under
    /// <paramref name="profile"/>, enqueuing the first <c>window_size</c> unwatched
    /// episodes. If another series already has an active window it is stopped first
    /// (its active job cancelled and its queued jobs cleared).
    /// </summary>
    Task StartWindowAsync(int mediaItemId, ProcessProfile profile);

    /// <summary>
    /// Stops the window for <paramref name="mediaItemId"/>: cancels the active job and
    /// clears the queue. No-op if that series has no active window.
    /// </summary>
    Task StopWindowAsync(int mediaItemId);

    /// <summary>
    /// Rotation trigger (RN-10): called when an episode is marked watched. If the
    /// episode belongs to the active series, its processed file is deleted (unless in
    /// use) and the next unwatched episode outside the window is enqueued, keeping the
    /// window at <c>window_size</c>. Episodes of other series are ignored.
    /// </summary>
    Task OnEpisodeWatchedAsync(int episodeId);

    /// <summary>The series with an active window, or <c>null</c> when idle.</summary>
    int? ActiveMediaItemId { get; }

    /// <summary>The profile of the active window, or <c>null</c> when idle.</summary>
    ProcessProfile? ActiveProfile { get; }

    /// <summary>Snapshot of the episodes currently in the window.</summary>
    List<Episode> WindowEpisodes { get; }
}

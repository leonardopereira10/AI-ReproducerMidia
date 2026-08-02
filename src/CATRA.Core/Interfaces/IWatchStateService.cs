using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Business layer for per-episode playback state (RF-07, RN-02, RN-03, RN-08).
/// Owns the dynamic watched-threshold calculation and the automatic mark-as-watched
/// rule, persists progress (always relative to the ORIGINAL file — RN-08) and exposes
/// the "Continuar Assistindo" query. Sits above <see cref="IWatchStateRepository"/>;
/// the UI-facing <c>ILibraryService</c> composes the same repository for read models.
/// </summary>
public interface IWatchStateService
{
    /// <summary>
    /// Persists the current playback position/progress for an episode and marks it
    /// watched automatically when the progress crosses the RN-02 threshold. A manual
    /// watched mark is never reverted by a later low-progress save.
    /// </summary>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="positionSec">Position in seconds (ORIGINAL file — RN-08).</param>
    /// <param name="durationSec">Total duration in seconds.</param>
    Task SaveProgressAsync(int episodeId, double positionSec, double durationSec);

    /// <summary>
    /// Evaluates the RN-02 threshold for the given position/duration and marks the
    /// episode watched when crossed (without requiring a full progress save).
    /// </summary>
    Task CheckAndMarkWatchedAsync(int episodeId, double positionSec, double durationSec);

    /// <summary>Flips the watched flag (RF-07 manual context-menu toggle).</summary>
    /// <exception cref="ArgumentException">Episode id not found.</exception>
    Task ToggleWatchedAsync(int episodeId);

    /// <summary>Explicitly sets the watched flag (RF-07). Marking watched sets progress to 100%.</summary>
    /// <exception cref="ArgumentException">Episode id not found.</exception>
    Task MarkWatchedAsync(int episodeId, bool watched);

    /// <summary>Returns the persisted state for an episode, or <c>null</c>.</summary>
    Task<WatchState?> GetStateAsync(int episodeId);

    /// <summary>
    /// RN-03: episodes eligible for "Continuar Assistindo" — progress started, not
    /// watched, progress below 85% and position past 30s — most recently updated first.
    /// </summary>
    Task<List<Episode>> GetContinueWatchingAsync();

    /// <summary>
    /// RN-03: whether "Continuar de onde parou" should be offered for a single episode
    /// (progress &gt; 0, not watched, progress &lt; 85% and position &gt; 30s).
    /// </summary>
    Task<bool> ShouldOfferContinueAsync(int episodeId);
}

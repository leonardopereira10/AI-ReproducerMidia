using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Playback;

/// <summary>
/// Business layer for per-episode playback state (RF-07, RN-02, RN-03, RN-08).
/// Calculates the dynamic watched threshold, auto-marks episodes watched when the
/// progress crosses it, persists progress relative to the ORIGINAL file (RN-08) and
/// answers the "Continuar Assistindo" query. Composes the repositories directly; the
/// UI-facing <c>LibraryService</c> reuses the same <see cref="IWatchStateRepository"/>
/// for its read models.
/// </summary>
/// <remarks>
/// The repository calls are synchronous SQLite operations wrapped in completed tasks,
/// matching the <c>LibraryService</c> pattern. The threshold rule (RN-02):
/// <list type="bullet">
/// <item>duration &lt; 180s (3min) → 95%</item>
/// <item>duration ≤ 300s (5min) → 90%</item>
/// <item>otherwise → (duration − 90s abertura − 90s encerramento) / duration</item>
/// </list>
/// </remarks>
public sealed class WatchStateService : IWatchStateService
{
    /// <summary>RN-03: "Continuar Assistindo" requires progress below this percentage.</summary>
    public const double ContinueMaxProgressPct = 85d;

    /// <summary>RN-03: "Continuar Assistindo" requires a position past this many seconds.</summary>
    public const double ContinueMinPositionSec = 30d;

    /// <summary>RN-02: short clips (&lt; 3min) need 95% to count as watched.</summary>
    private const double ShortClipThreshold = 0.95d;

    /// <summary>RN-02: medium clips (≤ 5min) need 90% to count as watched.</summary>
    private const double MediumClipThreshold = 0.90d;

    /// <summary>RN-02: opening credit allowance subtracted from the duration (seconds).</summary>
    private const double OpeningSec = 90d;

    /// <summary>RN-02: ending credit allowance subtracted from the duration (seconds).</summary>
    private const double EndingSec = 90d;

    private readonly IWatchStateRepository _watchStates;
    private readonly IEpisodeRepository _episodes;

    /// <summary>Creates the service with its dependencies.</summary>
    public WatchStateService(IWatchStateRepository watchStates, IEpisodeRepository episodes)
    {
        _watchStates = watchStates ?? throw new ArgumentNullException(nameof(watchStates));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
    }

    /// <summary>
    /// RN-02 (pure): watched threshold as a fraction (0–1) of the duration.
    /// 22min → ~86.4%, 5min → 90%, 2min → 95%, 2h → 97.5%.
    /// </summary>
    public static double CalculateThreshold(double durationSec)
    {
        if (durationSec < 180d)
        {
            return ShortClipThreshold;
        }

        if (durationSec <= 300d)
        {
            return MediumClipThreshold;
        }

        return (durationSec - OpeningSec - EndingSec) / durationSec;
    }

    /// <inheritdoc />
    public Task SaveProgressAsync(int episodeId, double positionSec, double durationSec)
    {
        // Edge case: no reliable duration yet (probe pending). Persist the position
        // only — never compute progress or auto-mark from a zero/unknown duration.
        if (durationSec <= 0d)
        {
            SavePositionOnly(episodeId, positionSec);
            return Task.CompletedTask;
        }

        double fraction = Math.Clamp(positionSec / durationSec, 0d, 1d);
        bool crossed = fraction >= CalculateThreshold(durationSec);
        Upsert(episodeId, positionSec, fraction * 100d, crossed);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CheckAndMarkWatchedAsync(int episodeId, double positionSec, double durationSec)
    {
        if (durationSec <= 0d)
        {
            return Task.CompletedTask;
        }

        double fraction = Math.Clamp(positionSec / durationSec, 0d, 1d);
        if (fraction < CalculateThreshold(durationSec))
        {
            return Task.CompletedTask;
        }

        Upsert(episodeId, positionSec, fraction * 100d, markWatched: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ToggleWatchedAsync(int episodeId)
    {
        EnsureEpisodeExists(episodeId);

        var state = _watchStates.GetByEpisodeId(episodeId);
        if (state is null)
        {
            _watchStates.Insert(new WatchState
            {
                EpisodeId = episodeId,
                Watched = true,
                ProgressPct = 100d,
            });
        }
        else
        {
            state.Watched = !state.Watched;
            if (state.Watched)
            {
                state.ProgressPct = 100d;
            }

            _watchStates.Update(state);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkWatchedAsync(int episodeId, bool watched)
    {
        EnsureEpisodeExists(episodeId);

        var state = _watchStates.GetByEpisodeId(episodeId);
        if (state is null)
        {
            _watchStates.Insert(new WatchState
            {
                EpisodeId = episodeId,
                Watched = watched,
                ProgressPct = watched ? 100d : 0d,
            });
        }
        else
        {
            state.Watched = watched;
            if (watched)
            {
                state.ProgressPct = 100d;
            }

            _watchStates.Update(state);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WatchState?> GetStateAsync(int episodeId) =>
        Task.FromResult(_watchStates.GetByEpisodeId(episodeId));

    /// <inheritdoc />
    public Task<List<Episode>> GetContinueWatchingAsync()
    {
        var episodes = _watchStates.GetInProgress()
            .Where(IsContinueWatching)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => _episodes.GetById(s.EpisodeId))
            .Where(e => e is not null)
            .Cast<Episode>()
            .ToList();

        return Task.FromResult(episodes);
    }

    /// <inheritdoc />
    public Task<bool> ShouldOfferContinueAsync(int episodeId)
    {
        var state = _watchStates.GetByEpisodeId(episodeId);
        return Task.FromResult(state is not null && IsContinueWatching(state));
    }

    /// <summary>RN-03 filter shared by the list query and the per-episode check.</summary>
    private static bool IsContinueWatching(WatchState state) =>
        !state.Watched &&
        state.ProgressPct > 0d &&
        state.ProgressPct < ContinueMaxProgressPct &&
        state.LastPositionSec > ContinueMinPositionSec;

    private void SavePositionOnly(int episodeId, double positionSec)
    {
        var state = _watchStates.GetByEpisodeId(episodeId);
        if (state is null)
        {
            _watchStates.Insert(new WatchState
            {
                EpisodeId = episodeId,
                LastPositionSec = positionSec,
            });
        }
        else
        {
            state.LastPositionSec = positionSec;
            _watchStates.Update(state);
        }
    }

    private void Upsert(int episodeId, double positionSec, double progressPct, bool markWatched)
    {
        var state = _watchStates.GetByEpisodeId(episodeId);
        if (state is null)
        {
            _watchStates.Insert(new WatchState
            {
                EpisodeId = episodeId,
                Watched = markWatched,
                ProgressPct = progressPct,
                LastPositionSec = positionSec,
            });
        }
        else
        {
            state.LastPositionSec = positionSec;
            state.ProgressPct = progressPct;
            // A manual watched mark is never reverted by a later low-progress save.
            if (markWatched)
            {
                state.Watched = true;
            }

            _watchStates.Update(state);
        }
    }

    private void EnsureEpisodeExists(int episodeId)
    {
        if (_episodes.GetById(episodeId) is null)
        {
            throw new ArgumentException($"Episode {episodeId} not found.", nameof(episodeId));
        }
    }
}

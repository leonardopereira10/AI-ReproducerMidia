using System.Globalization;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.WebControl;

/// <summary>
/// Orchestrates <see cref="ICastingService"/>, <see cref="ISlidingWindowService"/>
/// and the domain repositories to maintain a consolidated <see cref="WebControlState"/>
/// and translate incoming <see cref="WebControlCommand"/>s into player-engine calls.
/// <para>
/// Thread-safety: all mutable state is guarded by <see cref="_lock"/>. Event
/// handlers from <see cref="ICastingService"/> may fire on background threads;
/// they acquire the lock before mutating state and raise the public events
/// outside the lock to avoid re-entrancy deadlocks.
/// </para>
/// </summary>
public sealed class WebControlService : IWebControlService, IDisposable
{
    // ── Dependencies ──────────────────────────────────────────
    private readonly ICastingService _casting;
    private readonly ISlidingWindowService _slidingWindow;
    private readonly IEpisodeRepository _episodes;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IAppSettingsRepository _appSettings;
    private readonly IThumbnailService _thumbnails;

    // ── Lock ──────────────────────────────────────────────────
    private readonly object _lock = new();

    // ── Mutable state (guarded by _lock) ─────────────────────
    private int _currentEpisodeId;
    private Episode? _currentEpisode;
    private MediaItem? _currentMediaItem;
    private double _positionSec;
    private double _durationSec;
    private int _volume = 100;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _disposed;

    // ── Constants (mirror PlayerViewModel) ────────────────────
    private const double DefaultSkipIntroSec = 85.0;
    private const double SkipIntroEndGuardSec = 30.0;

    /// <summary>
    /// Creates a new <see cref="WebControlService"/> wired to the given dependencies.
    /// </summary>
    public WebControlService(
        ICastingService casting,
        ISlidingWindowService slidingWindow,
        IEpisodeRepository episodes,
        IMediaItemRepository mediaItems,
        IAppSettingsRepository appSettings,
        IThumbnailService thumbnails)
    {
        _casting = casting ?? throw new ArgumentNullException(nameof(casting));
        _slidingWindow = slidingWindow ?? throw new ArgumentNullException(nameof(slidingWindow));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));

        // Subscribe to casting events (may fire on background threads).
        _casting.StateChanged += OnCastingStateChanged;
        _casting.PositionChanged += OnCastingPositionChanged;
        _casting.MediaEnded += OnCastingMediaEnded;
    }

    // ════════════════════════════════════════════════════════════
    //  IWebControlService
    // ════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public WebControlState GetCurrentState()
    {
        lock (_lock)
        {
            return BuildStateSnapshot();
        }
    }

    /// <inheritdoc />
    public async Task HandleCommandAsync(WebControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        switch (command.Type)
        {
            case "play":
                await _casting.PlayAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    _isPlaying = true;
                    _isPaused = false;
                }
                RaiseStateChanged();
                break;

            case "pause":
                await _casting.PauseAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    _isPaused = true;
                }
                RaiseStateChanged();
                break;

            case "seek" when command.Position.HasValue:
                await _casting.SeekAsync(TimeSpan.FromSeconds(command.Position.Value)).ConfigureAwait(false);
                break;

            case "volume" when command.Level.HasValue:
                var clampedLevel = Math.Clamp(command.Level.Value, 0, 100);
                await _casting.SetVolumeAsync(clampedLevel).ConfigureAwait(false);
                lock (_lock)
                {
                    _volume = clampedLevel;
                }
                break;

            case "skipIntro":
                await HandleSkipIntroAsync().ConfigureAwait(false);
                break;

            case "nextEpisode":
                await HandleEpisodeNavigationAsync(forward: true).ConfigureAwait(false);
                break;

            case "previousEpisode":
                await HandleEpisodeNavigationAsync(forward: false).ConfigureAwait(false);
                break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Public events
    // ════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public event EventHandler<WebControlState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<(double Position, double Duration)>? PositionChanged;

    // ════════════════════════════════════════════════════════════
    //  SetCurrentEpisode — called by PlayerViewModel
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Informs the service which episode is currently loaded in the player.
    /// Loads the <see cref="Episode"/> and <see cref="MediaItem"/> from
    /// repositories, updates all derived state and raises <see cref="StateChanged"/>.
    /// </summary>
    /// <param name="episodeId">Database id of the current episode.</param>
    public void SetCurrentEpisode(int episodeId)
    {
        var episode = _episodes.GetById(episodeId);
        MediaItem? mediaItem = null;
        if (episode is not null)
        {
            mediaItem = _mediaItems.GetById(episode.MediaItemId);
        }

        lock (_lock)
        {
            _currentEpisodeId = episodeId;
            _currentEpisode = episode;
            _currentMediaItem = mediaItem;

            if (episode?.DurationSec.HasValue == true)
            {
                _durationSec = episode.DurationSec.Value;
            }
        }

        RaiseStateChanged();
    }

    // ════════════════════════════════════════════════════════════
    //  Casting event handlers
    // ════════════════════════════════════════════════════════════

    private void OnCastingStateChanged(object? sender, CastingState state)
    {
        lock (_lock)
        {
            _isPlaying = state == CastingState.Streaming;
            // When casting goes Idle (stopped) while we have an episode, mark as paused.
            // When streaming starts, clear the paused flag.
            if (state == CastingState.Streaming)
            {
                _isPaused = false;
            }
            else if (state == CastingState.Idle && _currentEpisodeId != 0)
            {
                _isPaused = true;
            }
        }

        RaiseStateChanged();
    }

    private void OnCastingPositionChanged(object? sender, TimeSpan position)
    {
        double posSec;
        double durSec;

        lock (_lock)
        {
            posSec = position.TotalSeconds;
            _positionSec = posSec;
            durSec = _durationSec;
        }

        PositionChanged?.Invoke(this, (posSec, durSec));
    }

    private void OnCastingMediaEnded(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _isPlaying = false;
            _isPaused = false;
        }

        RaiseStateChanged();
    }

    // ════════════════════════════════════════════════════════════
    //  SkipIntro logic (mirrors PlayerViewModel.CanSkipIntroAt)
    // ════════════════════════════════════════════════════════════

    private async Task HandleSkipIntroAsync()
    {
        double currentPos;
        double duration;
        double skipSec;

        lock (_lock)
        {
            currentPos = _positionSec;
            duration = _durationSec;
            skipSec = _currentMediaItem?.SkipIntroSec ?? GetDefaultSkipIntroSec();
        }

        var posTs = TimeSpan.FromSeconds(currentPos);
        var durTs = TimeSpan.FromSeconds(duration);

        if (!CanSkipIntroAt(posTs, durTs, skipSec))
        {
            return;
        }

        var targetSec = currentPos + skipSec;
        await _casting.SeekAsync(TimeSpan.FromSeconds(targetSec)).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure function — same logic as <c>PlayerViewModel.CanSkipIntroAt</c>.
    /// Returns <c>true</c> when <c>position + skip ≤ duration − 30s</c>.
    /// </summary>
    private static bool CanSkipIntroAt(TimeSpan position, TimeSpan duration, double skipIntroSec)
    {
        if (duration <= TimeSpan.Zero || skipIntroSec <= 0d)
        {
            return false;
        }

        return position + TimeSpan.FromSeconds(skipIntroSec)
            <= duration - TimeSpan.FromSeconds(SkipIntroEndGuardSec);
    }

    // ════════════════════════════════════════════════════════════
    //  Episode navigation (next / previous)
    // ════════════════════════════════════════════════════════════

    private async Task HandleEpisodeNavigationAsync(bool forward)
    {
        Episode? targetEpisode;

        lock (_lock)
        {
            if (_currentEpisodeId == 0)
            {
                return;
            }

            targetEpisode = forward
                ? _episodes.GetNextEpisode(_currentEpisodeId)
                : _episodes.GetPreviousEpisode(_currentEpisodeId);
        }

        if (targetEpisode is null)
        {
            return;
        }

        // Capture current device so we can resume casting after the switch.
        var device = _casting.CurrentDevice;

        // Stop current casting session.
        await _casting.StopCastingAsync().ConfigureAwait(false);

        // Update current episode state.
        var mediaItem = _mediaItems.GetById(targetEpisode.MediaItemId);

        lock (_lock)
        {
            _currentEpisodeId = targetEpisode.Id;
            _currentEpisode = targetEpisode;
            _currentMediaItem = mediaItem;
            _positionSec = 0;
            _durationSec = targetEpisode.DurationSec ?? 0;
            _isPlaying = false;
            _isPaused = false;
        }

        RaiseStateChanged();

        // Resume casting if a device was active.
        if (device is not null)
        {
            await StartCastingForCurrentEpisodeAsync(device).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the media file for the current episode and starts casting to
    /// <paramref name="device"/>. Uses <see cref="ProcessProfile.Dlna"/> when
    /// the sliding window has a DLNA profile active, otherwise falls back to
    /// the original file path.
    /// </summary>
    private async Task StartCastingForCurrentEpisodeAsync(DlnaDeviceInfo device)
    {
        Episode? episode;
        MediaItem? mediaItem;

        lock (_lock)
        {
            episode = _currentEpisode;
            mediaItem = _currentMediaItem;
        }

        if (episode is null)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(episode.DisplayTitle)
            ? episode.FileName
            : episode.DisplayTitle;

        var duration = TimeSpan.FromSeconds(episode.DurationSec ?? 0);

        await _casting.StartCastingAsync(device, episode.FilePath, title, duration)
            .ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════
    //  State snapshot builder
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a <see cref="WebControlState"/> from the current mutable state.
    /// Must be called while holding <see cref="_lock"/>.
    /// </summary>
    private WebControlState BuildStateSnapshot()
    {
        var title = _currentEpisode is not null
            ? (string.IsNullOrWhiteSpace(_currentEpisode.DisplayTitle)
                ? _currentEpisode.FileName
                : _currentEpisode.DisplayTitle)
            : string.Empty;

        var skipSec = _currentMediaItem?.SkipIntroSec ?? GetDefaultSkipIntroSec();
        var canSkip = CanSkipIntroAt(
            TimeSpan.FromSeconds(_positionSec),
            TimeSpan.FromSeconds(_durationSec),
            skipSec);

        var hasNext = _currentEpisodeId != 0 && _episodes.GetNextEpisode(_currentEpisodeId) is not null;
        var hasPrev = _currentEpisodeId != 0 && _episodes.GetPreviousEpisode(_currentEpisodeId) is not null;

        var castDeviceName = _casting.CurrentDevice?.FriendlyName;

        // Build queue from sliding window.
        var queue = BuildQueue();

        return new WebControlState(
            IsPlaying: _isPlaying,
            IsPaused: _isPaused,
            Position: _positionSec,
            Duration: _durationSec,
            Volume: _volume,
            Title: title,
            ThumbnailUrl: ResolveThumbnailUrl(),
            SkipIntroSec: skipSec,
            CanSkipIntro: canSkip,
            HasNextEpisode: hasNext,
            HasPreviousEpisode: hasPrev,
            CastDeviceName: castDeviceName,
            ProfileLabel: _slidingWindow.ActiveProfile?.ToString(),
            Queue: queue);
    }

    /// <summary>
    /// Builds the queue list from <see cref="ISlidingWindowService.WindowEpisodes"/>,
    /// marking the current episode with <c>IsCurrent = true</c>.
    /// Must be called while holding <see cref="_lock"/>.
    /// </summary>
    private List<WebControlQueueItem> BuildQueue()
    {
        var windowEpisodes = _slidingWindow.WindowEpisodes;
        if (windowEpisodes.Count == 0 && _currentEpisode is not null)
        {
            // Fallback: at least show the current episode.
            return
            [
                new WebControlQueueItem(
                    Id: _currentEpisode.Id,
                    Title: string.IsNullOrWhiteSpace(_currentEpisode.DisplayTitle)
                        ? _currentEpisode.FileName
                        : _currentEpisode.DisplayTitle,
                    DurationSec: _currentEpisode.DurationSec ?? 0,
                    IsCurrent: true)
            ];
        }

        var queue = new List<WebControlQueueItem>(windowEpisodes.Count);
        foreach (var ep in windowEpisodes)
        {
            queue.Add(new WebControlQueueItem(
                Id: ep.Id,
                Title: string.IsNullOrWhiteSpace(ep.DisplayTitle) ? ep.FileName : ep.DisplayTitle,
                DurationSec: ep.DurationSec ?? 0,
                IsCurrent: ep.Id == _currentEpisodeId));
        }

        return queue;
    }

    /// <summary>
    /// Resolves thumbnail URL for the current episode.
    /// Uses <see cref="Episode.ThumbnailPath"/> when available.
    /// Must be called while holding <see cref="_lock"/>.
    /// </summary>
    private string? ResolveThumbnailUrl()
    {
        if (_currentEpisode?.ThumbnailPath is { } path && !string.IsNullOrEmpty(path))
        {
            return path;
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the default skip-intro seconds from <see cref="IAppSettingsRepository"/>,
    /// falling back to <see cref="DefaultSkipIntroSec"/> (85s).
    /// </summary>
    private double GetDefaultSkipIntroSec()
    {
        var raw = _appSettings.Get(AppSettingsModel.DefaultSkipIntroSecKey);
        if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val > 0)
        {
            return val;
        }

        return DefaultSkipIntroSec;
    }

    /// <summary>
    /// Raises <see cref="StateChanged"/> outside the lock to avoid re-entrancy issues.
    /// </summary>
    private void RaiseStateChanged()
    {
        var snapshot = GetCurrentState();
        StateChanged?.Invoke(this, snapshot);
    }

    // ════════════════════════════════════════════════════════════
    //  IDisposable
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Unsubscribes from <see cref="ICastingService"/> events.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _casting.StateChanged -= OnCastingStateChanged;
        _casting.PositionChanged -= OnCastingPositionChanged;
        _casting.MediaEnded -= OnCastingMediaEnded;
    }
}

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
    private readonly IStreamService? _streamService;
    private readonly ILibraryApiService? _libraryApiService;
    private readonly IWatchStateRepository? _watchStates;

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

    // ── Browser-mode state (guarded by _lock) ────────────────
    private string? _playerClientId;
    private string? _currentStreamUrl;
    private string _activeProfile = "original";
    private string _mode = "idle"; // "browser" | "dlna" | "idle"
    private string? _currentStreamToken;

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
        IThumbnailService thumbnails,
        IStreamService? streamService = null,
        ILibraryApiService? libraryApiService = null,
        IWatchStateRepository? watchStates = null)
    {
        _casting = casting ?? throw new ArgumentNullException(nameof(casting));
        _slidingWindow = slidingWindow ?? throw new ArgumentNullException(nameof(slidingWindow));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _streamService = streamService;
        _libraryApiService = libraryApiService;
        _watchStates = watchStates;

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
                await DispatchTransportCommandAsync(command).ConfigureAwait(false);
                lock (_lock)
                {
                    _isPlaying = true;
                    _isPaused = false;
                }
                RaiseStateChanged();
                break;

            case "pause":
                await DispatchTransportCommandAsync(command).ConfigureAwait(false);
                lock (_lock)
                {
                    _isPaused = true;
                }
                RaiseStateChanged();
                break;

            case "seek" when command.Position.HasValue:
                await DispatchTransportCommandAsync(command).ConfigureAwait(false);
                break;

            case "volume" when command.Level.HasValue:
                var clampedLevel = Math.Clamp(command.Level.Value, 0, 100);
                await DispatchTransportCommandAsync(command).ConfigureAwait(false);
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

            // ── Browser-mode commands ───────────────────────────

            case "playEpisode":
                await HandlePlayEpisodeAsync(command).ConfigureAwait(false);
                break;

            case "switchProfile":
                await HandleSwitchProfileAsync(command).ConfigureAwait(false);
                break;

            case "reportProgress":
                HandleReportProgress(command);
                break;

            case "ended":
                await HandleEndedAsync().ConfigureAwait(false);
                break;

            case "ready":
                HandleReady(command);
                break;

            case "browse":
                await HandleBrowseAsync(command).ConfigureAwait(false);
                break;

            case "castTo":
                await HandleCastToAsync(command).ConfigureAwait(false);
                break;

            case "stopCast":
            case "stopCasting":
                await HandleStopCastAsync().ConfigureAwait(false);
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

    /// <inheritdoc />
    public event EventHandler<WebControlCommand>? CommandForPlayer;

    /// <inheritdoc />
    public event EventHandler<(string Target, object Data)>? LibraryDataReady;

    /// <inheritdoc />
    public string? PlayerClientId
    {
        get { lock (_lock) { return _playerClientId; } }
    }

    /// <inheritdoc />
    public void SetPlayerClient(string? clientId)
    {
        lock (_lock)
        {
            _playerClientId = clientId;
        }
    }

    /// <inheritdoc />
    public Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync()
        => _casting.DiscoverDevicesAsync();

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
            skipSec = GetDefaultSkipIntroSec();
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

        // Browser mode: swap the stream to the new episode so the panel player
        // actually loads it. Without this the state keeps carrying the old
        // streamUrl, the <video> retains the previous episode's frame while the
        // metadata (title/thumbnail) already points at the new episode — the
        // panel appears to stack the new episode over the old one's frame.
        if (_streamService is not null)
        {
            string mode;
            string? oldToken;
            string preferredProfile;
            lock (_lock)
            {
                mode = _mode;
                oldToken = _currentStreamToken;
                preferredProfile = _activeProfile ?? "original";
            }

            if (mode == "browser")
            {
                var resolution = _streamService.ResolveEpisode(targetEpisode.Id, preferredProfile)
                    ?? _streamService.ResolveEpisode(targetEpisode.Id, "original");

                if (resolution is not null)
                {
                    if (oldToken is not null)
                    {
                        _streamService.Unregister(oldToken);
                    }

                    lock (_lock)
                    {
                        _currentStreamUrl = resolution.StreamUrl;
                        _activeProfile = resolution.Profile;
                        _currentStreamToken = resolution.Token;
                        _isPlaying = true;
                        _isPaused = false;
                    }
                }
            }
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
    //  Browser-mode command handlers
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Handles <c>playEpisode</c>: resolves the stream for the requested episode
    /// and profile, switches to browser mode, and raises state changed.
    /// <para>
    /// When a DLNA transmission is active the backend owns the session: the
    /// command does NOT stop it. Same episode ⇒ no-op (page refresh while
    /// casting keeps the TV playing); different episode ⇒ the casting session
    /// moves to the new episode on the same renderer.
    /// </para>
    /// </summary>
    private async Task HandlePlayEpisodeAsync(WebControlCommand command)
    {
        if (command.EpisodeId is null)
        {
            return;
        }

        // ── Backend-owned transmission guard ─────────────────────
        string prevMode;
        lock (_lock)
        {
            prevMode = _mode;
        }

        if (prevMode == "dlna")
        {
            var device = _casting.CurrentDevice;
            if (device is not null)
            {
                int currentEp;
                lock (_lock)
                {
                    currentEp = _currentEpisodeId;
                }

                if (currentEp == command.EpisodeId.Value)
                {
                    // Page refresh / reconnect while casting: keep the
                    // transmission running and just re-sync the panel.
                    RaiseStateChanged();
                    return;
                }

                await SwitchCastingToEpisodeAsync(command.EpisodeId.Value, device)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (_streamService is null)
        {
            return;
        }

        var profile = command.Profile ?? "original";
        var resolution = _streamService.ResolveEpisode(command.EpisodeId.Value, profile);
        if (resolution is null)
        {
            return; // profile not available
        }

        // Unregister previous stream token if any.
        string? oldToken;
        lock (_lock)
        {
            oldToken = _currentStreamToken;
        }

        if (oldToken is not null)
        {
            _streamService.Unregister(oldToken);
        }

        // Load episode and media item.
        var episode = _episodes.GetById(command.EpisodeId.Value);
        MediaItem? mediaItem = null;
        if (episode is not null)
        {
            mediaItem = _mediaItems.GetById(episode.MediaItemId);
        }

        lock (_lock)
        {
            // _playerClientId is set by the WebSocket handler via SetPlayerClient
            // BEFORE this method runs; do not clear it here or transport command
            // relay (play/pause/seek/volume) to the browser player silently breaks.
            _mode = "browser";
            _currentStreamUrl = resolution.StreamUrl;
            _activeProfile = resolution.Profile;
            _currentStreamToken = resolution.Token;
            _currentEpisodeId = command.EpisodeId.Value;
            _currentEpisode = episode;
            _currentMediaItem = mediaItem;
            _positionSec = 0;
            _durationSec = episode?.DurationSec ?? 0;
            _isPlaying = true;
            _isPaused = false;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Handles <c>switchProfile</c>: resolves the same episode under a different
    /// profile and updates the stream URL. The player client receives the new
    /// URL and seeks to the current position.
    /// </summary>
    private Task HandleSwitchProfileAsync(WebControlCommand command)
    {
        if (_streamService is null || command.Profile is null)
        {
            return Task.CompletedTask;
        }

        string currentMode;
        int episodeId;

        lock (_lock)
        {
            currentMode = _mode;
            episodeId = _currentEpisodeId;
        }

        if (currentMode != "browser" || episodeId == 0)
        {
            return Task.CompletedTask;
        }

        var resolution = _streamService.ResolveEpisode(episodeId, command.Profile);
        if (resolution is null)
        {
            return Task.CompletedTask;
        }

        // Unregister old token.
        string? oldToken;
        lock (_lock)
        {
            oldToken = _currentStreamToken;
        }

        if (oldToken is not null)
        {
            _streamService.Unregister(oldToken);
        }

        lock (_lock)
        {
            _currentStreamUrl = resolution.StreamUrl;
            _activeProfile = resolution.Profile;
            _currentStreamToken = resolution.StreamUrl;
            // Preserve _positionSec so the player client can seek to it.
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles <c>reportProgress</c>: updates position and persists WatchState.
    /// </summary>
    private void HandleReportProgress(WebControlCommand command)
    {
        if (!command.Position.HasValue)
        {
            return;
        }

        int episodeId;
        double duration;

        lock (_lock)
        {
            _positionSec = command.Position.Value;
            episodeId = _currentEpisodeId;
            duration = _durationSec;
        }

        PositionChanged?.Invoke(this, (command.Position.Value, duration));

        // Persist WatchState.
        PersistWatchState(episodeId, command.Position.Value, duration, ended: false);
    }

    /// <summary>
    /// Handles <c>ended</c>: marks playback as stopped, persists WatchState at 100%.
    /// </summary>
    private async Task HandleEndedAsync()
    {
        int episodeId;
        double duration;

        lock (_lock)
        {
            _isPlaying = false;
            _isPaused = false;
            episodeId = _currentEpisodeId;
            duration = _durationSec;
        }

        PersistWatchState(episodeId, duration, duration, ended: true);

        RaiseStateChanged();

        // Browser mode: auto-advance to the next episode, matching the desktop
        // player behaviour (OnEngineMediaEnded navigates automatically).
        bool autoAdvance;
        lock (_lock)
        {
            autoAdvance = _mode == "browser"
                && _currentEpisodeId != 0
                && _episodes.GetNextEpisode(_currentEpisodeId) is not null;
        }

        if (autoAdvance)
        {
            await HandleEpisodeNavigationAsync(forward: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles <c>ready</c>: the player client reports its duration via the Position field.
    /// </summary>
    private void HandleReady(WebControlCommand command)
    {
        if (command.Position.HasValue && command.Position.Value > 0)
        {
            lock (_lock)
            {
                _durationSec = command.Position.Value;
            }
        }
    }

    /// <summary>
    /// Handles <c>browse</c>: delegates to <see cref="ILibraryApiService"/> based
    /// on <see cref="WebControlCommand.Target"/> and raises <see cref="LibraryDataReady"/>.
    /// </summary>
    private async Task HandleBrowseAsync(WebControlCommand command)
    {
        if (_libraryApiService is null || command.Target is null)
        {
            return;
        }

        object? data = command.Target switch
        {
            "categories" => await _libraryApiService.GetCategoriesAsync().ConfigureAwait(false),
            "items" when command.CategoryId.HasValue
                => await _libraryApiService.GetItemsByCategoryAsync(command.CategoryId.Value).ConfigureAwait(false),
            "episodes" when command.ItemId.HasValue
                => await _libraryApiService.GetEpisodesByItemAsync(command.ItemId.Value).ConfigureAwait(false),
            "search" when !string.IsNullOrWhiteSpace(command.Profile)
                => await _libraryApiService.SearchAsync(command.Profile).ConfigureAwait(false),
            "continueWatching"
                => await _libraryApiService.GetContinueWatchingAsync().ConfigureAwait(false),
            _ => null
        };

        if (data is not null)
        {
            LibraryDataReady?.Invoke(this, (command.Target, data));
        }
    }

    /// <summary>
    /// Handles <c>castTo</c>: switches from browser mode to DLNA mode.
    /// Clears browser state, discovers devices, and starts casting.
    /// </summary>
    private async Task HandleCastToAsync(WebControlCommand command)
    {
        if (command.DeviceUdn is null)
        {
            return;
        }

        // Clear browser state if switching from browser mode.
        string prevMode;
        string? oldToken;

        lock (_lock)
        {
            prevMode = _mode;
            oldToken = _currentStreamToken;
        }

        if (prevMode == "browser")
        {
            if (oldToken is not null && _streamService is not null)
            {
                _streamService.Unregister(oldToken);
            }

            lock (_lock)
            {
                _playerClientId = null;
                _currentStreamUrl = null;
                _currentStreamToken = null;
            }
        }

        lock (_lock)
        {
            _mode = "dlna";
        }

        // Discover and find the target device.
        var devices = await _casting.DiscoverDevicesAsync().ConfigureAwait(false);
        var device = devices.FirstOrDefault(d =>
            string.Equals(d.Udn, command.DeviceUdn, StringComparison.OrdinalIgnoreCase));

        if (device is not null && _currentEpisodeId != 0)
        {
            await StartCastingForCurrentEpisodeAsync(device).ConfigureAwait(false);
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Moves an active DLNA casting session to <paramref name="episodeId"/> on
    /// the same <paramref name="device"/>. Used when a <c>playEpisode</c>
    /// arrives while a transmission is already running — the backend keeps
    /// ownership of the session instead of handing it to the browser.
    /// </summary>
    private async Task SwitchCastingToEpisodeAsync(int episodeId, DlnaDeviceInfo device)
    {
        var episode = _episodes.GetById(episodeId);
        if (episode is null)
        {
            RaiseStateChanged();
            return;
        }

        var mediaItem = _mediaItems.GetById(episode.MediaItemId);

        await _casting.StopCastingAsync().ConfigureAwait(false);

        lock (_lock)
        {
            _currentEpisodeId = episode.Id;
            _currentEpisode = episode;
            _currentMediaItem = mediaItem;
            _positionSec = 0;
            _durationSec = episode.DurationSec ?? 0;
            _isPlaying = false;
            _isPaused = false;
        }

        await StartCastingForCurrentEpisodeAsync(device).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>
    /// Handles <c>stopCast</c> / <c>stopCasting</c>: stops DLNA casting and
    /// transitions to idle mode.
    /// </summary>
    private async Task HandleStopCastAsync()
    {
        await _casting.StopCastingAsync().ConfigureAwait(false);

        lock (_lock)
        {
            _mode = "idle";
            _isPlaying = false;
            _isPaused = false;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Persists a <see cref="WatchState"/> for the given episode.
    /// No-op when <see cref="IWatchStateRepository"/> is not available or episode id is 0.
    /// </summary>
    private void PersistWatchState(int episodeId, double positionSec, double durationSec, bool ended)
    {
        if (_watchStates is null || episodeId == 0)
        {
            return;
        }

        var pct = durationSec > 0 ? Math.Min(100, (positionSec / durationSec) * 100) : 0;
        if (ended)
        {
            pct = 100;
        }

        var existing = _watchStates.GetByEpisodeId(episodeId);
        if (existing is not null)
        {
            existing.LastPositionSec = positionSec;
            existing.ProgressPct = pct;
            existing.Watched = ended || pct >= 95;
            existing.UpdatedAt = DateTime.UtcNow;
            _watchStates.Update(existing);
        }
        else
        {
            _watchStates.Insert(new WatchState
            {
                EpisodeId = episodeId,
                LastPositionSec = positionSec,
                ProgressPct = pct,
                Watched = ended || pct >= 95,
                UpdatedAt = DateTime.UtcNow
            });
        }
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

        var skipSec = GetDefaultSkipIntroSec();
        var canSkip = CanSkipIntroAt(
            TimeSpan.FromSeconds(_positionSec),
            TimeSpan.FromSeconds(_durationSec),
            skipSec);

        var hasNext = _currentEpisodeId != 0 && _episodes.GetNextEpisode(_currentEpisodeId) is not null;
        var hasPrev = _currentEpisodeId != 0 && _episodes.GetPreviousEpisode(_currentEpisodeId) is not null;

        var castDeviceName = _casting.CurrentDevice?.FriendlyName;

        // Build queue from sliding window.
        var queue = BuildQueue();

        var seriesTitle = _currentMediaItem?.Title;
        var availableProfiles = _currentEpisodeId != 0 && _streamService is not null
            ? _streamService.GetAvailableProfiles(_currentEpisodeId)
            : null;

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
            ProfileLabel: _mode == "browser" ? _activeProfile : _slidingWindow.ActiveProfile?.ToString(),
            Queue: queue,
            Mode: _mode,
            StreamUrl: _currentStreamUrl,
            SeriesTitle: seriesTitle,
            AvailableProfiles: availableProfiles,
            AvailableDevices: null, // populated on demand via discover command
            IsPlayerClient: false, // set by WebSocketHandler per-connection
            EpisodeId: _currentEpisodeId); // internal: lets PlayerViewModel follow panel-driven navigation
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
    /// Returns a relative API URL (<c>/api/thumbnail/{id}</c>) that the
    /// <see cref="WebControlServer"/> serves as image content.
    /// Must be called while holding <see cref="_lock"/>.
    /// </summary>
    private string? ResolveThumbnailUrl()
    {
        if (_currentEpisode is not null
            && _currentEpisode.Id > 0
            && !string.IsNullOrEmpty(_currentEpisode.ThumbnailPath))
        {
            return $"/api/thumbnail/{_currentEpisode.Id}";
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════
    //  Transport dispatch (DLNA vs browser)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Dispatches a transport command (play/pause/seek/volume) to either the
    /// DLNA casting service or the browser player client, depending on the
    /// current mode. In browser mode, raises <see cref="CommandForPlayer"/>
    /// so the WebSocket handler can relay the command to the player client.
    /// In DLNA mode, calls the corresponding <see cref="ICastingService"/> method.
    /// </summary>
    private async Task DispatchTransportCommandAsync(WebControlCommand command)
    {
        string mode;
        lock (_lock)
        {
            mode = _mode;
        }

        if (mode == "browser")
        {
            // Relay to player client via event.
            CommandForPlayer?.Invoke(this, command);
            return;
        }

        // DLNA path.
        switch (command.Type)
        {
            case "play":
                await _casting.PlayAsync().ConfigureAwait(false);
                break;
            case "pause":
                await _casting.PauseAsync().ConfigureAwait(false);
                break;
            case "seek" when command.Position.HasValue:
                await _casting.SeekAsync(TimeSpan.FromSeconds(command.Position.Value)).ConfigureAwait(false);
                break;
            case "volume" when command.Level.HasValue:
                var clamped = Math.Clamp(command.Level.Value, 0, 100);
                await _casting.SetVolumeAsync(clamped).ConfigureAwait(false);
                break;
        }
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

        // Unregister active stream token.
        string? token;
        lock (_lock)
        {
            token = _currentStreamToken;
            _currentStreamToken = null;
        }

        if (token is not null && _streamService is not null)
        {
            _streamService.Unregister(token);
        }
    }
}

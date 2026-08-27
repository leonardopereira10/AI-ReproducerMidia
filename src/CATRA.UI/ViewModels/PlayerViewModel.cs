using System.Collections.ObjectModel;
using System.Globalization;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.UI.Converters;
using CATRA.UI.Navigation;
using CATRA.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CATRA.UI.ViewModels;

/// <summary>
/// Player screen (Tela 4, RF-05): custom transport over the ST-05
/// <see cref="IPlaybackEngine"/>. Exposes position/duration/volume/state as
/// bindables plus Play/Pause/Stop/Seek/SkipIntro commands, the "Continuar de
/// MM:SS?" resume decision (RN-03) and the skip-intro end guard (RN-04).
/// </summary>
/// <remarks>
/// Fully unit-testable: no WPF types are used. Engine events arrive on
/// background threads and are marshalled through the <see cref="SynchronizationContext"/>
/// captured at construction (the UI dispatcher context in the app, <c>null</c> —
/// inline execution — in headless tests).
/// </remarks>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>Global default intro skip length in seconds (RN-04: 1:25).</summary>
    public const double DefaultSkipIntroSec = 85.0;

    /// <summary>RN-03: resume is offered below this watched percentage.</summary>
    public const double ResumeMaxProgressPct = 85.0;

    /// <summary>RN-03: resume is offered only past this position (seconds).</summary>
    public const double ResumeMinPositionSec = 30.0;

    /// <summary>RN-04: skip intro is disabled this close to the end (seconds).</summary>
    public const double SkipIntroEndGuardSec = 30.0;

    /// <summary>RF-05: progress is persisted on this interval (seconds).</summary>
    public const double ProgressSaveIntervalSec = 5.0;

    /// <summary>
    /// RN-08: processed and original durations must match within this tolerance
    /// (seconds). Interpolation changes fps/resolution but never the total time,
    /// so the position mapping is 1:1; a larger gap is logged for diagnosis.
    /// </summary>
    public const double DurationMatchToleranceSec = 1.0;

    /// <summary>ST-08: discovery retry cadence while the device dropdown is open.</summary>
    public const double CastDiscoveryRetrySec = 10.0;

    private readonly IPlaybackEngine _engine;
    private readonly IEpisodeRepository _episodes;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IWatchStateRepository _watchStates;
    private readonly IWatchStateService _watchStateService;
    private readonly IDialogService _dialogs;
    private readonly IAppNavigator _navigator;
    private readonly ICastingService _casting;
    private readonly IMediaFileResolver _mediaFileResolver;
    private readonly IWebControlService? _webControlService;
    private readonly IAppSettingsRepository? _appSettings;
    private readonly SynchronizationContext? _uiContext;

    private Episode? _episode;
    private string? _filePath;
    private string _castProfileLabel = string.Empty;
    private double _skipIntroSec = DefaultSkipIntroSec;
    private double _volumeBeforeMute = 100.0;
    private bool _applyingVolume;
    private bool _closed;
    private bool _detached;
    private CancellationTokenSource? _progressSaverCts;
    private CancellationTokenSource? _discoveryRetryCts;
    private int? _nextEpisodeId;
    private int? _previousEpisodeId;

    /// <summary>Creates the view model and subscribes to the engine events.</summary>
    public PlayerViewModel(
        IPlaybackEngine engine,
        IEpisodeRepository episodes,
        IMediaItemRepository mediaItems,
        IWatchStateRepository watchStates,
        IWatchStateService watchStateService,
        IDialogService dialogs,
        IAppNavigator navigator,
        ICastingService casting,
        IMediaFileResolver mediaFileResolver,
        IWebControlService? webControlService = null,
        IAppSettingsRepository? appSettings = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _watchStates = watchStates ?? throw new ArgumentNullException(nameof(watchStates));
        _watchStateService = watchStateService ?? throw new ArgumentNullException(nameof(watchStateService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _casting = casting ?? throw new ArgumentNullException(nameof(casting));
        _mediaFileResolver = mediaFileResolver ?? throw new ArgumentNullException(nameof(mediaFileResolver));
        _webControlService = webControlService;
        _appSettings = appSettings;

        _uiContext = SynchronizationContext.Current;

        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.MediaEnded += OnEngineMediaEnded;
        _engine.Error += OnEngineError;

        _casting.StateChanged += OnCastingStateChanged;
        _casting.PositionChanged += OnCastingPositionChanged;
        _casting.MediaEnded += OnCastingMediaEnded;
    }

    /// <summary>Current playback position.</summary>
    [ObservableProperty]
    private TimeSpan _position;

    /// <summary>Total duration of the open media.</summary>
    [ObservableProperty]
    private TimeSpan _duration;

    /// <summary>Position in seconds (seek-bar value; user-draggable).</summary>
    [ObservableProperty]
    private double _positionSeconds;

    /// <summary>Duration in seconds (seek-bar maximum).</summary>
    [ObservableProperty]
    private double _durationSeconds;

    /// <summary>Output volume, 0–100.</summary>
    [ObservableProperty]
    private double _volume = 100.0;

    /// <summary>Whether audio is muted (volume forced to zero).</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>Current engine lifecycle state.</summary>
    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Stopped;

    /// <summary>Fullscreen toggle; the view applies/restores the window chrome.</summary>
    [ObservableProperty]
    private bool _isFullscreen;

    /// <summary>True while the user drags the seek bar (engine updates suppressed).</summary>
    [ObservableProperty]
    private bool _isSeeking;

    /// <summary>Episode display title shown in the player header/tooltip.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Playing-profile indicator (metadata-derived; transport bar).</summary>
    [ObservableProperty]
    private string _profileIndicator = "🖥 —";

    /// <summary>ST-20: resolved playing-profile label ("🖥 1080p135" / "📄 Original").</summary>
    [ObservableProperty]
    private string _profileLabel = string.Empty;

    /// <summary>ST-20: tooltip for the profile indicator.</summary>
    [ObservableProperty]
    private string _profileTooltip = "Arquivo original";

    /// <summary>ST-20: true when playing a processed file (drives the green indicator).</summary>
    [ObservableProperty]
    private bool _isProcessedProfile;

    /// <summary>Skip-intro button tooltip ("Pular +1:25").</summary>
    [ObservableProperty]
    private string _skipIntroTooltip = "Pular +1:25";

    /// <summary>Last playback error message, if any.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Whether <see cref="ErrorMessage"/> is set.</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>Video aspect ratio (width / height) used for letterboxing.</summary>
    [ObservableProperty]
    private double _aspectRatio = 16.0 / 9.0;

    /// <summary>ST-08: a DLNA casting session is active (renderer streaming).</summary>
    [ObservableProperty]
    private bool _isCasting;

    /// <summary>ST-08: the TV playback is paused (remote pause state tracker).</summary>
    [ObservableProperty]
    private bool _isCastPaused;

    /// <summary>ST-08: an SSDP discovery pass is running.</summary>
    [ObservableProperty]
    private bool _isDiscoveringDevices;

    /// <summary>ST-08: the device dropdown is open (drives discovery + 10s retry).</summary>
    [ObservableProperty]
    private bool _isCastDropdownOpen;

    /// <summary>ST-08: friendly name of the renderer being cast to.</summary>
    [ObservableProperty]
    private string? _castDeviceName;

    /// <summary>ST-08: status bar text ("Transmitindo para {device} [{position}]").</summary>
    [ObservableProperty]
    private string _castStatusText = string.Empty;

    /// <summary>ST-08: whether <see cref="CastDevices"/> has any entries.</summary>
    [ObservableProperty]
    private bool _hasCastDevices;

    /// <summary>ST-08: show "Nenhum dispositivo encontrado" in the dropdown.</summary>
    [ObservableProperty]
    private bool _showNoDevicesFound;

    /// <summary>ST-08: cast button label ("📺 Transmitir" / "📺 {device}").</summary>
    [ObservableProperty]
    private string _castButtonLabel = "📺 Transmitir";

    /// <summary>ST-08: renderers found by the last discovery pass.</summary>
    public ObservableCollection<DlnaDeviceInfo> CastDevices { get; } = new();

    /// <summary>Whether there is a next episode available.</summary>
    [ObservableProperty]
    private bool _hasNextEpisode;

    /// <summary>Whether there is a previous episode available.</summary>
    [ObservableProperty]
    private bool _hasPreviousEpisode;

    /// <summary>The episode being played, once opened.</summary>
    public Episode? Episode => _episode;

    /// <summary>Intro skip length in seconds for the current series (RN-04).</summary>
    public double SkipIntroSec => _skipIntroSec;

    /// <summary>
    /// Loads the episode, opens it on the engine and starts playback, offering
    /// "Continuar de MM:SS?" when RN-03 applies.
    /// </summary>
    public async Task OpenAsync(int episodeId)
    {
        var episode = _episodes.GetById(episodeId);
        if (episode is null)
        {
            SetError($"Episódio {episodeId} não encontrado.");
            return;
        }

        var mediaItem = _mediaItems.GetById(episode.MediaItemId);
        var watchState = _watchStates.GetByEpisodeId(episodeId);

        // A previous media may still be open (engine requires Stopped to open).
        if (_engine.State != PlaybackState.Stopped)
        {
            _engine.Stop();
        }

        // ST-20 (RF-05): prefer the processed local file, falling back to the
        // original. When a processed output was expected but is unusable (RN-09
        // stale / missing file) the user confirms the fallback to the original.
        var resolved = await _mediaFileResolver.ResolveAsync(episodeId, ProcessProfile.Local);
        if (!resolved.IsProcessed && resolved.FellBackFromProcessed)
        {
            bool useOriginal = _dialogs.Confirm(
                "Arquivo processado indisponível",
                "Arquivo processado não disponível. Reproduzir original?",
                "Reproduzir Original",
                "Cancelar");
            if (!useOriginal)
            {
                return;
            }
        }

        _episode = episode;
        _filePath = resolved.FilePath;
        _skipIntroSec = GetDefaultSkipIntroSec();
        SkipIntroTooltip = $"Pular +{TimeSpanToStringConverter.Format(TimeSpan.FromSeconds(_skipIntroSec))}";
        Title = string.IsNullOrWhiteSpace(episode.DisplayTitle) ? episode.FileName : episode.DisplayTitle;
        ErrorMessage = null;
        HasError = false;
        ProfileLabel = resolved.DisplayLabel;
        IsProcessedProfile = resolved.IsProcessed;
        ProfileTooltip = resolved.IsProcessed
            ? "Arquivo processado (RIFE + FSR 4)"
            : "Arquivo original";

        VideoMetadata metadata;
        try
        {
            metadata = await _engine.OpenAsync(resolved.FilePath);
        }
        catch (Exception ex)
        {
            SetError($"Falha ao abrir a mídia: {ex.Message}");
            return;
        }

        Duration = metadata.Duration;
        DurationSeconds = metadata.Duration.TotalSeconds;
        AspectRatio = metadata.Height > 0 ? (double)metadata.Width / metadata.Height : 16.0 / 9.0;
        ProfileIndicator = BuildProfileIndicator(metadata);
        ValidateDurationMatch(episode, metadata.Duration);

        double resumeAtSec = ResolveResumePosition(watchState);

        _engine.Play();
        if (resumeAtSec > 0d)
        {
            // Seek after Play: while Stopped the engine ignores seeks.
            _engine.Seek(TimeSpan.FromSeconds(resumeAtSec));
            Position = TimeSpan.FromSeconds(resumeAtSec);
            PositionSeconds = resumeAtSec;
        }

        StartProgressSaver();
        ResolveAdjacentEpisodes(episodeId);
        SkipIntroCommand.NotifyCanExecuteChanged();
        TransmitCommand.NotifyCanExecuteChanged();

        // ST-10: notify the web control service of the current episode
        // so the panel can display it even before casting starts.
        _webControlService?.SetCurrentEpisode(episodeId);
    }

    /// <summary>Binds the video renderer to the hosted child window (ST-05).</summary>
    public void AttachVideoOutput(IntPtr windowHandle) => _engine.SetOutputWindow(windowHandle);

    /// <summary>Notifies the renderer of the new output size in device pixels.</summary>
    public void ResizeVideoOutput(int width, int height) => _engine.ResizeOutput(width, height);

    /// <summary>
    /// RN-03 (pure): whether "Continuar de MM:SS?" must be offered.
    /// </summary>
    public static bool ShouldOfferResume(double progressPct, double lastPositionSec)
        => progressPct < ResumeMaxProgressPct && lastPositionSec > ResumeMinPositionSec;

    /// <summary>
    /// RN-04 (pure): whether skip intro is allowed at <paramref name="position"/>
    /// (disabled when <c>position + skip &gt; duration - 30s</c>).
    /// </summary>
    public static bool CanSkipIntroAt(TimeSpan position, TimeSpan duration, double skipIntroSec)
    {
        if (duration <= TimeSpan.Zero || skipIntroSec <= 0d)
        {
            return false;
        }

        return position + TimeSpan.FromSeconds(skipIntroSec)
            <= duration - TimeSpan.FromSeconds(SkipIntroEndGuardSec);
    }

    /// <summary>
    /// Reads the default skip-intro seconds from <see cref="IAppSettingsRepository"/>,
    /// falling back to <see cref="DefaultSkipIntroSec"/> (85s).
    /// </summary>
    private double GetDefaultSkipIntroSec()
    {
        var raw = _appSettings?.Get(AppSettingsModel.DefaultSkipIntroSecKey);
        if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val > 0)
        {
            return val;
        }

        return DefaultSkipIntroSec;
    }

    /// <summary>
    /// Decides the start position for the just-opened media, showing the
    /// resume dialog when RN-03 applies. Returns seconds to seek to (0 = start).
    /// </summary>
    internal double ResolveResumePosition(WatchState? watchState)
    {
        if (watchState is null ||
            !ShouldOfferResume(watchState.ProgressPct, watchState.LastPositionSec))
        {
            return 0d;
        }

        bool resume = _dialogs.Confirm(
            "Continuar assistindo",
            $"Continuar de {TimeSpanToStringConverter.Format(TimeSpan.FromSeconds(watchState.LastPositionSec))}?",
            "Continuar",
            "Do início");

        return resume ? watchState.LastPositionSec : 0d;
    }

    /// <summary>Play (re-opening the media when it ended / was stopped).
    /// While casting (ST-08) the command drives the TV instead of the local engine.</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsCasting)
        {
            IsCastPaused = false;
            await _casting.PlayAsync();
            return;
        }

        if (_engine.State == PlaybackState.Playing)
        {
            return;
        }

        if (_engine.State == PlaybackState.Stopped && _filePath is not null)
        {
            // MediaEnded/Stop released per-media resources: Play requires a re-open.
            try
            {
                await _engine.OpenAsync(_filePath);
            }
            catch (Exception ex)
            {
                SetError($"Falha ao reabrir a mídia: {ex.Message}");
                return;
            }
        }

        _engine.Play();
    }

    /// <summary>Pause (remote while casting — ST-08).</summary>
    [RelayCommand]
    private void Pause()
    {
        if (IsCasting)
        {
            IsCastPaused = true;
            _ = _casting.PauseAsync();
            return;
        }

        _engine.Pause();
    }

    /// <summary>Play/pause toggle bound to the ▶/⏸ button.</summary>
    [RelayCommand]
    private async Task PlayPauseToggleAsync()
    {
        if (IsCasting)
        {
            if (IsCastPaused)
            {
                await PlayAsync();
            }
            else
            {
                Pause();
            }

            return;
        }

        if (_engine.State == PlaybackState.Playing)
        {
            _engine.Pause();
        }
        else
        {
            await PlayAsync();
        }
    }

    /// <summary>Stop (releases per-media resources; position resets).</summary>
    [RelayCommand]
    private void Stop()
    {
        SaveCurrentProgress(); // RF-05: persist final progress before the position resets.
        StopProgressSaver();
        _engine.Stop();
        Position = TimeSpan.Zero;
        PositionSeconds = 0d;
        SkipIntroCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Seeks to <paramref name="seconds"/> (clamped to the duration).
    /// While casting (ST-08) the seek goes to the TV via REL_TIME.</summary>
    [RelayCommand]
    private void Seek(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0d)
        {
            seconds = 0d;
        }

        if (Duration > TimeSpan.Zero && seconds > Duration.TotalSeconds)
        {
            seconds = Duration.TotalSeconds;
        }

        if (IsCasting)
        {
            Position = TimeSpan.FromSeconds(seconds);
            PositionSeconds = seconds;
            _ = _casting.SeekAsync(TimeSpan.FromSeconds(seconds));
            SkipIntroCommand.NotifyCanExecuteChanged();
            return;
        }

        _engine.Seek(TimeSpan.FromSeconds(seconds));
        Position = TimeSpan.FromSeconds(seconds);
        PositionSeconds = seconds;
        SkipIntroCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Marks the start of a user seek-bar drag (engine updates suppressed).</summary>
    [RelayCommand]
    private void BeginSeek() => IsSeeking = true;

    /// <summary>Commits the dragged position to the engine.</summary>
    [RelayCommand]
    private void EndSeek()
    {
        if (!IsSeeking)
        {
            return;
        }

        IsSeeking = false;
        Seek(PositionSeconds);
    }

    /// <summary>RN-04: advances <c>current + SkipIntroSec</c>.
    /// While casting (ST-08) the seek goes to the TV via REL_TIME and the
    /// current position is read from the casting position tracker (the local
    /// engine is paused and reports a stale position).</summary>
    [RelayCommand(CanExecute = nameof(CanSkipIntro))]
    private void SkipIntro()
    {
        var currentPosition = IsCasting ? Position : _engine.GetPosition();
        var target = currentPosition + TimeSpan.FromSeconds(_skipIntroSec);

        if (IsCasting)
        {
            Position = target;
            PositionSeconds = target.TotalSeconds;
            _ = _casting.SeekAsync(target);
        }
        else
        {
            _engine.Seek(target);
            Position = target;
            PositionSeconds = target.TotalSeconds;
        }

        SkipIntroCommand.NotifyCanExecuteChanged();
    }

    private bool CanSkipIntro()
    {
        var currentPosition = IsCasting ? Position : _engine.GetPosition();
        return CanSkipIntroAt(currentPosition, Duration, _skipIntroSec);
    }

    /// <summary>Toggles mute, remembering the previous volume.</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            ApplyVolume(_volumeBeforeMute > 0d ? _volumeBeforeMute : 100.0);
        }
        else
        {
            _volumeBeforeMute = Volume > 0d ? Volume : 100.0;
            IsMuted = true;
            ApplyVolume(0d);
        }
    }

    /// <summary>Toggles <see cref="IsFullscreen"/>; the view reacts to the change.</summary>
    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    /// <summary>
    /// ST-08 (RF-06): toggles the DLNA device dropdown. Enabled only with a
    /// loaded file; opening starts discovery with a 10s retry while open.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTransmit))]
    private void Transmit() => IsCastDropdownOpen = !IsCastDropdownOpen;

    private bool CanTransmit() => _filePath is not null;

    /// <summary>ST-08: runs one SSDP discovery pass and refreshes <see cref="CastDevices"/>.</summary>
    [RelayCommand]
    private async Task RefreshCastDevicesAsync()
    {
        if (IsDiscoveringDevices)
        {
            return;
        }

        IsDiscoveringDevices = true;
        try
        {
            var devices = await _casting.DiscoverDevicesAsync();
            RunOnUi(() =>
            {
                CastDevices.Clear();
                foreach (var device in devices)
                {
                    CastDevices.Add(device);
                }

                HasCastDevices = CastDevices.Count > 0;
                UpdateNoDevicesFound();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => SetError($"Falha ao procurar dispositivos DLNA: {ex.Message}"));
        }
        finally
        {
            RunOnUi(() => IsDiscoveringDevices = false);
        }
    }

    /// <summary>
    /// ST-08: starts casting the open file to <paramref name="device"/>. The
    /// local player pauses (does not stop) while the TV streams.
    /// </summary>
    [RelayCommand]
    private async Task CastToDeviceAsync(DlnaDeviceInfo? device)
    {
        if (device is null || _filePath is null || _episode is null)
        {
            return;
        }

        // ST-20 (RF-06): prefer the processed DLNA file, falling back to the
        // original. When a processed output was expected but is unusable the
        // user confirms the fallback before the transmission starts.
        var resolved = await _mediaFileResolver.ResolveAsync(_episode.Id, ProcessProfile.Dlna);
        if (!resolved.IsProcessed && resolved.FellBackFromProcessed)
        {
            bool useOriginal = _dialogs.Confirm(
                "Transmissão",
                "Arquivo processado (4K 55fps) não disponível. Transmitir original?",
                "Transmitir Original",
                "Cancelar");
            if (!useOriginal)
            {
                return;
            }
        }

        _castProfileLabel = resolved.DisplayLabel;
        IsCastDropdownOpen = false;
        _engine.Pause(); // RF-06: local playback pauses, not stops.

        try
        {
            await _casting.StartCastingAsync(device, resolved.FilePath, Title, Duration);
            IsCastPaused = false;
        }
        catch (Exception ex)
        {
            SetError($"Falha ao transmitir: {ex.Message}");
        }
    }

    /// <summary>ST-08: stops the casting session (server keeps running).</summary>
    [RelayCommand]
    private async Task StopCastingAsync()
    {
        try
        {
            await _casting.StopCastingAsync();
        }
        catch (Exception ex)
        {
            SetError($"Falha ao parar a transmissão: {ex.Message}");
        }
    }

    /// <summary>ST-08: remote play (casting dropdown control).</summary>
    [RelayCommand]
    private async Task CastPlayAsync()
    {
        IsCastPaused = false;
        await _casting.PlayAsync();
    }

    /// <summary>ST-08: remote pause (casting dropdown control).</summary>
    [RelayCommand]
    private async Task CastPauseAsync()
    {
        IsCastPaused = true;
        await _casting.PauseAsync();
    }

    /// <summary>Skips to the next episode in the series (if available).</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextEpisodeAsync()
    {
        if (_nextEpisodeId is not int nextId)
        {
            return;
        }

        await NavigateToEpisodeAsync(nextId);
    }

    private bool CanGoNext() => _nextEpisodeId.HasValue;

    /// <summary>Skips to the previous episode in the series (if available).</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousEpisodeAsync()
    {
        if (_previousEpisodeId is not int prevId)
        {
            return;
        }

        await NavigateToEpisodeAsync(prevId);
    }

    private bool CanGoPrevious() => _previousEpisodeId.HasValue;

    /// <summary>
    /// Navigates to the given episode, stopping the current playback and casting session.
    /// If a casting session was active, automatically resumes transmission to the same device.
    /// </summary>
    private async Task NavigateToEpisodeAsync(int episodeId)
    {
        // Capture the current casting device before stopping
        var castDevice = _casting.CurrentDevice;
        bool wasCasting = IsCasting;

        // Stop current playback and casting before navigating
        StopCastingFireAndForget();
        SaveCurrentProgress();
        _engine.Stop();

        // Navigate to the new episode (reuses the same player page)
        await OpenAsync(episodeId);

        // Resume casting to the same device if it was active
        if (wasCasting && castDevice is not null)
        {
            await ResumeCastingToDeviceAsync(castDevice);
        }
    }

    /// <summary>
    /// Resumes casting to the specified device after episode navigation.
    /// Resolves the DLNA file and starts transmission without user interaction.
    /// </summary>
    private async Task ResumeCastingToDeviceAsync(DlnaDeviceInfo device)
    {
        if (_episode is null || _filePath is null)
        {
            return;
        }

        try
        {
            // Resolve the DLNA file for the new episode
            var resolved = await _mediaFileResolver.ResolveAsync(_episode.Id, ProcessProfile.Dlna);
            
            // If processed file is not available, fall back to original without asking
            // (user already confirmed fallback on the first cast, so we reuse that decision)
            _castProfileLabel = resolved.DisplayLabel;
            _engine.Pause(); // RF-06: local playback pauses, not stops.

            await _casting.StartCastingAsync(device, resolved.FilePath, Title, Duration);
            IsCastPaused = false;
        }
        catch (Exception ex)
        {
            SetError($"Falha ao retomar transmissão: {ex.Message}");
        }
    }

    /// <summary>Stops playback and navigates back (idempotent).</summary>
    [RelayCommand]
    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        StopCastingFireAndForget(); // ST-08: never leave the TV streaming on exit.
        SaveCurrentProgress(); // RF-05: persist final progress on close.
        _engine.Stop();
        Detach();
        _navigator.GoBack();
    }

    /// <summary>
    /// Stops playback and unsubscribes from the (singleton) engine. Called by
    /// the view when it unloads; safe to call multiple times.
    /// </summary>
    public void ReleasePlayback()
    {
        if (_closed)
        {
            return;
        }

        StopCastingFireAndForget(); // ST-08: stop any active casting session.
        SaveCurrentProgress(); // RF-05: persist final progress on release.
        _engine.Stop();
        Detach();
    }

    /// <summary>Unsubscribes from the engine events (idempotent).</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        StopProgressSaver();
        StopDiscoveryRetry();
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.MediaEnded -= OnEngineMediaEnded;
        _engine.Error -= OnEngineError;
        _casting.StateChanged -= OnCastingStateChanged;
        _casting.PositionChanged -= OnCastingPositionChanged;
        _casting.MediaEnded -= OnCastingMediaEnded;
    }

    /// <summary>
    /// Implements <see cref="IDisposable"/> so the navigation service can detach
    /// this (transient) VM from the singleton engine/casting events when the
    /// player page is left (ST-19 follow-up leak fix). Delegates to the
    /// idempotent <see cref="Detach"/>; safe to call multiple times.
    /// </summary>
    public void Dispose() => Detach();

    partial void OnVolumeChanged(double value)
    {
        if (_applyingVolume)
        {
            return;
        }

        _engine.SetVolume((float)(Math.Clamp(value, 0d, 100d) / 100d));
        if (IsCasting)
        {
            // ST-08: the slider drives the TV volume while casting.
            _ = _casting.SetVolumeAsync((int)Math.Round(Math.Clamp(value, 0d, 100d)));
        }

        if (value > 0d && IsMuted)
        {
            IsMuted = false;
        }
    }

    /// <summary>ST-08: dropdown open/close drives discovery + the 10s retry loop.</summary>
    partial void OnIsDiscoveringDevicesChanged(bool value) => UpdateNoDevicesFound();

    private void UpdateNoDevicesFound()
        => ShowNoDevicesFound = !IsDiscoveringDevices && !HasCastDevices && !IsCasting;

    partial void OnIsCastDropdownOpenChanged(bool value)
    {
        StopDiscoveryRetry();
        if (value && _filePath is not null)
        {
            var cts = new CancellationTokenSource();
            _discoveryRetryCts = cts;
            _ = Task.Run(() => DiscoveryRetryLoopAsync(cts.Token));
        }
    }

    private async Task DiscoveryRetryLoopAsync(CancellationToken token)
    {
        await RefreshCastDevicesAsync();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CastDiscoveryRetrySec));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await RefreshCastDevicesAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Dropdown closed: retry loop ends.
        }
    }

    private void StopDiscoveryRetry()
    {
        var cts = _discoveryRetryCts;
        _discoveryRetryCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StopCastingFireAndForget()
    {
        if (!IsCasting && _casting.State == CastingState.Idle)
        {
            return;
        }

        _casting
            .StopCastingAsync()
            .ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    private void OnCastingStateChanged(object? sender, CastingState state)
        => RunOnUi(() =>
        {
            IsCasting = state == CastingState.Streaming;
            CastDeviceName = _casting.CurrentDevice?.FriendlyName;
            CastButtonLabel = IsCasting ? $"📺 {CastDeviceName}" : "📺 Transmitir";
            UpdateNoDevicesFound();
            CastStatusText = state switch
            {
                CastingState.Connecting => "Conectando ao dispositivo...",
                CastingState.Streaming => BuildCastStatusText(CastDeviceName, null),
                CastingState.Error => _casting.ErrorMessage ?? "Erro na transmissão",
                _ => string.Empty,
            };

            // ST-10: when casting starts, notify the web control service of
            // the current episode so the web panel can track next/previous.
            if (state == CastingState.Streaming && _episode is not null)
            {
                _webControlService?.SetCurrentEpisode(_episode.Id);
            }

            if (state == CastingState.Error)
            {
                SetError(CastStatusText);
            }

            if (state == CastingState.Idle)
            {
                IsCastPaused = false;
            }

            TransmitCommand.NotifyCanExecuteChanged();
        });

    private void OnCastingPositionChanged(object? sender, TimeSpan position)
        => RunOnUi(() =>
        {
            if (!IsCasting)
            {
                return;
            }

            if (IsSeeking)
            {
                return; // user is dragging the seek bar: keep the preview value
            }

            Position = position;
            PositionSeconds = position.TotalSeconds;
            if (!string.IsNullOrEmpty(CastDeviceName))
            {
                CastStatusText = BuildCastStatusText(CastDeviceName, position);
            }

            SkipIntroCommand.NotifyCanExecuteChanged();
        });

    private void ApplyVolume(double value)
    {
        _applyingVolume = true;
        try
        {
            Volume = value;
        }
        finally
        {
            _applyingVolume = false;
        }

        _engine.SetVolume((float)(Math.Clamp(value, 0d, 100d) / 100d));
    }

    private void OnEnginePositionChanged(object? sender, TimeSpan e)
    {
        RunOnUi(() =>
        {
            if (IsSeeking)
            {
                return; // user is dragging: keep the preview value
            }

            Position = e;
            PositionSeconds = e.TotalSeconds;
            SkipIntroCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnEngineStateChanged(object? sender, PlaybackState e)
        => RunOnUi(() => State = e);

    private void OnEngineMediaEnded(object? sender, EventArgs e)
        => RunOnUi(async () =>
        {
            State = PlaybackState.Stopped;
            if (Duration > TimeSpan.Zero)
            {
                Position = Duration;
                PositionSeconds = Duration.TotalSeconds;
            }

            StopProgressSaver();
            SaveCurrentProgress(); // natural end: position == duration -> marks watched (RN-02).

            // Auto-play next episode immediately if available
            if (_nextEpisodeId.HasValue)
            {
                await NavigateToEpisodeAsync(_nextEpisodeId.Value);
            }
        });

    /// <summary>ST-08: raised when the DLNA renderer reaches the end of the current episode.</summary>
    private void OnCastingMediaEnded(object? sender, EventArgs e)
        => RunOnUi(async () =>
        {
            // Auto-play next episode immediately if available (casting mode)
            if (_nextEpisodeId.HasValue)
            {
                await NavigateToEpisodeAsync(_nextEpisodeId.Value);
            }
        });

    private void OnEngineError(object? sender, PlaybackErrorEventArgs e)
        => RunOnUi(() => SetError(e.Message));

    private void SetError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    /// <summary>
    /// RF-05: starts the periodic (every <see cref="ProgressSaveIntervalSec"/>) progress
    /// saver. The loop runs on the thread pool and never throws into the UI.
    /// </summary>
    private void StartProgressSaver()
    {
        StopProgressSaver();
        var cts = new CancellationTokenSource();
        _progressSaverCts = cts;
        _ = Task.Run(() => ProgressSaveLoopAsync(cts.Token));
    }

    private async Task ProgressSaveLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(ProgressSaveIntervalSec));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                SaveCurrentProgress();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the saver was stopped.
        }
        catch
        {
            // Never let the background saver crash the process.
        }
    }

    private void StopProgressSaver()
    {
        var cts = _progressSaverCts;
        _progressSaverCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// RF-05 / RN-08: fire-and-forget persistence of the current position (ORIGINAL-file
    /// based — the processed file shares the same duration, so the mapping is 1:1). The
    /// service also auto-marks watched when the RN-02 threshold is crossed. Wrapped so a
    /// persistence failure can never crash the UI.
    /// </summary>
    private void SaveCurrentProgress()
    {
        try
        {
            var episode = _episode;
            if (episode is null || DurationSeconds <= 0d)
            {
                return;
            }

            double position = Math.Clamp(PositionSeconds, 0d, DurationSeconds);
            _watchStateService
                .SaveProgressAsync(episode.Id, position, DurationSeconds)
                .ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }
        catch
        {
            // Best effort: progress persistence must never crash the UI.
        }
    }

    private static string BuildProfileIndicator(VideoMetadata metadata)
    {
        // Placeholder format (real profile indicator: ST-20).
        string fps = metadata.Fps > 0d ? ((int)Math.Round(metadata.Fps)).ToString() : "?";
        return metadata.Height > 0 ? $"🖥 {metadata.Height}p{fps}" : "🖥 —";
    }

    /// <summary>
    /// ST-20: builds the DLNA status-bar text, appending the resolved profile
    /// label ("Transmitindo para {device} | {label} [position]").
    /// </summary>
    private string BuildCastStatusText(string? deviceName, TimeSpan? position)
    {
        string text = $"Transmitindo para {deviceName}";
        if (!string.IsNullOrEmpty(_castProfileLabel))
        {
            text += $" | {_castProfileLabel}";
        }

        if (position is TimeSpan pos)
        {
            text += $" [{TimeSpanToStringConverter.Format(pos)}]";
        }

        return text;
    }

    /// <summary>
    /// RN-08: the processed file shares the original's total duration (interpolation
    /// adds frames, never time), so the position mapping is 1:1 and no conversion is
    /// needed. When the library knows the original duration, a gap beyond
    /// <see cref="DurationMatchToleranceSec"/> is logged for diagnosis.
    /// </summary>
    private static void ValidateDurationMatch(Episode episode, TimeSpan openedDuration)
    {
        if (episode.DurationSec is not double expectedSec || expectedSec <= 0d)
        {
            return; // Unknown original duration: nothing to validate against.
        }

        double delta = Math.Abs(openedDuration.TotalSeconds - expectedSec);
        if (delta > DurationMatchToleranceSec)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[PlayerViewModel] RN-08 duration mismatch for episode {episode.Id}: " +
                $"original {expectedSec:F1}s vs opened {openedDuration.TotalSeconds:F1}s " +
                $"(delta {delta:F1}s > {DurationMatchToleranceSec:F1}s); timestamp mapping may drift.");
        }
    }

    /// <summary>
    /// Resolves the next and previous episode IDs for the current episode
    /// and updates the HasNextEpisode/HasPreviousEpisode properties.
    /// </summary>
    private void ResolveAdjacentEpisodes(int episodeId)
    {
        var next = _episodes.GetNextEpisode(episodeId);
        var prev = _episodes.GetPreviousEpisode(episodeId);

        _nextEpisodeId = next?.Id;
        _previousEpisodeId = prev?.Id;
        HasNextEpisode = next is not null;
        HasPreviousEpisode = prev is not null;

        NextEpisodeCommand.NotifyCanExecuteChanged();
        PreviousEpisodeCommand.NotifyCanExecuteChanged();
    }
}

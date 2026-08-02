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
public sealed partial class PlayerViewModel : ObservableObject
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

    private readonly IPlaybackEngine _engine;
    private readonly IEpisodeRepository _episodes;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IWatchStateRepository _watchStates;
    private readonly IWatchStateService _watchStateService;
    private readonly IDialogService _dialogs;
    private readonly IAppNavigator _navigator;
    private readonly SynchronizationContext? _uiContext;

    private Episode? _episode;
    private string? _filePath;
    private double _skipIntroSec = DefaultSkipIntroSec;
    private double _volumeBeforeMute = 100.0;
    private bool _applyingVolume;
    private bool _closed;
    private bool _detached;
    private CancellationTokenSource? _progressSaverCts;

    /// <summary>Creates the view model and subscribes to the engine events.</summary>
    public PlayerViewModel(
        IPlaybackEngine engine,
        IEpisodeRepository episodes,
        IMediaItemRepository mediaItems,
        IWatchStateRepository watchStates,
        IWatchStateService watchStateService,
        IDialogService dialogs,
        IAppNavigator navigator)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _watchStates = watchStates ?? throw new ArgumentNullException(nameof(watchStates));
        _watchStateService = watchStateService ?? throw new ArgumentNullException(nameof(watchStateService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));

        _uiContext = SynchronizationContext.Current;

        _engine.PositionChanged += OnEnginePositionChanged;
        _engine.StateChanged += OnEngineStateChanged;
        _engine.MediaEnded += OnEngineMediaEnded;
        _engine.Error += OnEngineError;
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

    /// <summary>Playing-profile indicator (placeholder until ST-20).</summary>
    [ObservableProperty]
    private string _profileIndicator = "🖥 —";

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

        _episode = episode;
        _filePath = episode.FilePath;
        _skipIntroSec = mediaItem?.SkipIntroSec ?? DefaultSkipIntroSec;
        SkipIntroTooltip = $"Pular +{TimeSpanToStringConverter.Format(TimeSpan.FromSeconds(_skipIntroSec))}";
        Title = string.IsNullOrWhiteSpace(episode.DisplayTitle) ? episode.FileName : episode.DisplayTitle;
        ErrorMessage = null;
        HasError = false;

        VideoMetadata metadata;
        try
        {
            metadata = await _engine.OpenAsync(episode.FilePath);
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
        SkipIntroCommand.NotifyCanExecuteChanged();
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

    /// <summary>Play (re-opening the media when it ended / was stopped).</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
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

    /// <summary>Pause.</summary>
    [RelayCommand]
    private void Pause() => _engine.Pause();

    /// <summary>Play/pause toggle bound to the ▶/⏸ button.</summary>
    [RelayCommand]
    private async Task PlayPauseToggleAsync()
    {
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

    /// <summary>Seeks to <paramref name="seconds"/> (clamped to the duration).</summary>
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

    /// <summary>RN-04: advances <c>current + SkipIntroSec</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanSkipIntro))]
    private void SkipIntro()
    {
        var target = _engine.GetPosition() + TimeSpan.FromSeconds(_skipIntroSec);
        _engine.Seek(target);
        Position = target;
        PositionSeconds = target.TotalSeconds;
        SkipIntroCommand.NotifyCanExecuteChanged();
    }

    private bool CanSkipIntro()
        => CanSkipIntroAt(_engine.GetPosition(), Duration, _skipIntroSec);

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

    /// <summary>Placeholder until ST-08 implements DLNA transmission.</summary>
    [RelayCommand]
    private void Transmit()
        => _dialogs.ShowMessage("Transmitir", "Transmissão DLNA estará disponível em ST-08.");

    /// <summary>Stops playback and navigates back (idempotent).</summary>
    [RelayCommand]
    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
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
        _engine.PositionChanged -= OnEnginePositionChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.MediaEnded -= OnEngineMediaEnded;
        _engine.Error -= OnEngineError;
    }

    partial void OnVolumeChanged(double value)
    {
        if (_applyingVolume)
        {
            return;
        }

        _engine.SetVolume((float)(Math.Clamp(value, 0d, 100d) / 100d));
        if (value > 0d && IsMuted)
        {
            IsMuted = false;
        }
    }

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
        => RunOnUi(() =>
        {
            State = PlaybackState.Stopped;
            if (Duration > TimeSpan.Zero)
            {
                Position = Duration;
                PositionSeconds = Duration.TotalSeconds;
            }

            StopProgressSaver();
            SaveCurrentProgress(); // natural end: position == duration -> marks watched (RN-02).
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
}

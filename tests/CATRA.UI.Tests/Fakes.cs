using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.UI.Navigation;
using CATRA.UI.Services;

namespace CATRA.UI.Tests;

/// <summary>
/// Recording <see cref="IPlaybackEngine"/> fake for player view-model tests.
/// Mimics the real engine's state transitions (Play/Pause/Stop raise
/// <see cref="StateChanged"/>) without FFmpeg/GPU/audio. Position/timer
/// events are raised explicitly by the tests.
/// </summary>
internal sealed class FakePlaybackEngine : IPlaybackEngine
{
    public VideoMetadata Metadata { get; set; } = new(
        Duration: TimeSpan.FromMinutes(22),
        Fps: 135,
        Width: 1920,
        Height: 1080,
        VideoCodec: "hevc",
        AudioCodec: "aac",
        Title: "fake",
        IsHardwareAccelerated: true);

    public Exception? ThrowOnOpen { get; set; }

    public List<string> OpenedPaths { get; } = new();

    public List<TimeSpan> SeekCalls { get; } = new();

    public List<float> VolumeCalls { get; } = new();

    public List<IntPtr> OutputWindows { get; } = new();

    public List<(int Width, int Height)> ResizeCalls { get; } = new();

    public int PlayCount { get; private set; }

    public int PauseCount { get; private set; }

    public int StopCount { get; private set; }

    public TimeSpan Position { get; private set; }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public IReadOnlyList<AudioTrack> AudioTracks { get; } = new List<AudioTrack>();

    VideoMetadata? IPlaybackEngine.Metadata => OpenedPaths.Count > 0 ? Metadata : null;

    public event EventHandler<TimeSpan>? PositionChanged;

    public event EventHandler<PlaybackState>? StateChanged;

    public event EventHandler? MediaEnded;

    public event EventHandler<PlaybackErrorEventArgs>? Error;

    public Task<VideoMetadata> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (ThrowOnOpen is not null)
        {
            throw ThrowOnOpen;
        }

        OpenedPaths.Add(filePath);
        Position = TimeSpan.Zero;
        return Task.FromResult(Metadata);
    }

    public void SetOutputWindow(IntPtr windowHandle) => OutputWindows.Add(windowHandle);

    public void ResizeOutput(int width, int height) => ResizeCalls.Add((width, height));

    public void Play()
    {
        PlayCount++;
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        PauseCount++;
        SetState(PlaybackState.Paused);
    }

    public void Stop()
    {
        StopCount++;
        Position = TimeSpan.Zero;
        SetState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        SeekCalls.Add(position);
        Position = position;
    }

    public void SetVolume(float volume) => VolumeCalls.Add(volume);

    public TimeSpan GetPosition() => Position;

    public void Dispose()
    {
    }

    /// <summary>Simulates the engine's 250ms position report.</summary>
    public void RaisePositionChanged(TimeSpan position)
    {
        Position = position;
        PositionChanged?.Invoke(this, position);
    }

    /// <summary>Simulates natural end of stream (engine back in Stopped).</summary>
    public void RaiseMediaEnded()
    {
        State = PlaybackState.Stopped;
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseError(string message)
        => Error?.Invoke(this, new PlaybackErrorEventArgs(message));

    private void SetState(PlaybackState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}

/// <summary>Scriptable <see cref="IDialogService"/> fake (no windows).</summary>
internal sealed class FakeDialogService : IDialogService
{
    public Queue<bool> ConfirmResults { get; } = new();

    public List<(string Title, string Message, string Accept, string Cancel)> ConfirmCalls { get; } = new();

    public List<(string Title, string Message)> Messages { get; } = new();

    public void ShowMessage(string title, string message)
        => Messages.Add((title, message));

    public bool Confirm(string title, string message, string acceptText, string cancelText)
    {
        ConfirmCalls.Add((title, message, acceptText, cancelText));
        return ConfirmResults.Count > 0 && ConfirmResults.Dequeue();
    }

    public string? Prompt(string title, string label, string initialValue) => null;

    public string? OpenFile(string title, string filter) => null;
}

/// <summary>
/// Recording <see cref="ICastingService"/> fake (ST-08). No SSDP/Kestrel/SOAP:
/// records calls and lets tests drive <see cref="StateChanged"/> /
/// <see cref="PositionChanged"/> explicitly. Default <see cref="State"/> is
/// <see cref="CastingState.Idle"/> so the player's casting paths stay dormant.
/// </summary>
internal sealed class FakeCastingService : ICastingService
{
    public CastingState State { get; private set; } = CastingState.Idle;

    public DlnaDeviceInfo? CurrentDevice { get; private set; }

    public string? ErrorMessage { get; private set; }

    public List<DlnaDeviceInfo> DevicesToReturn { get; } = new();

    public int DiscoverCount { get; private set; }

    public List<(DlnaDeviceInfo Device, string FilePath, string Title)> StartCalls { get; } = new();

    public int PlayCount { get; private set; }

    public int PauseCount { get; private set; }

    public int StopCount { get; private set; }

    public List<TimeSpan> SeekCalls { get; } = new();

    public List<int> VolumeCalls { get; } = new();

    public TimeSpan PositionToReturn { get; set; } = TimeSpan.Zero;

    public event EventHandler<CastingState>? StateChanged;

    public event EventHandler<TimeSpan>? PositionChanged;

    public Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync()
    {
        DiscoverCount++;
        return Task.FromResult(DevicesToReturn.ToList());
    }

    public Task StartCastingAsync(DlnaDeviceInfo device, string filePath, string title)
    {
        StartCalls.Add((device, filePath, title));
        CurrentDevice = device;
        RaiseStateChanged(CastingState.Streaming);
        return Task.CompletedTask;
    }

    public Task PlayAsync()
    {
        PlayCount++;
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        PauseCount++;
        return Task.CompletedTask;
    }

    public Task StopCastingAsync()
    {
        StopCount++;
        CurrentDevice = null;
        RaiseStateChanged(CastingState.Idle);
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        SeekCalls.Add(position);
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume)
    {
        VolumeCalls.Add(volume);
        return Task.CompletedTask;
    }

    public Task<TimeSpan> GetPositionAsync() => Task.FromResult(PositionToReturn);

    public void RaiseStateChanged(CastingState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void RaisePositionChanged(TimeSpan position)
        => PositionChanged?.Invoke(this, position);
}

/// <summary>Recording <see cref="IAppNavigator"/> fake.</summary>
internal sealed class FakeNavigator : IAppNavigator
{
    public int HomeCount { get; private set; }

    public int BackCount { get; private set; }

    public int SettingsCount { get; private set; }

    public List<int> DetailIds { get; } = new();

    public List<int> PlayerIds { get; } = new();

    public List<int?> QueueIds { get; } = new();

    public void GoToHome() => HomeCount++;

    public void GoToMediaDetail(int mediaItemId) => DetailIds.Add(mediaItemId);

    public void GoToPlayer(int episodeId) => PlayerIds.Add(episodeId);

    public void GoToSettings() => SettingsCount++;

    public void GoToProcessingQueue(int? mediaItemId = null) => QueueIds.Add(mediaItemId);

    public void GoBack() => BackCount++;
}

/// <summary>In-memory <see cref="IEpisodeRepository"/>.</summary>
internal sealed class FakeEpisodeRepository : IEpisodeRepository
{
    private readonly Dictionary<int, Episode> _byId = new();

    public void Add(Episode episode) => _byId[episode.Id] = episode;

    public IReadOnlyList<Episode> GetAll() => _byId.Values.ToList();

    public Episode? GetById(int id) => _byId.GetValueOrDefault(id);

    public Episode Insert(Episode entity)
    {
        _byId[entity.Id] = entity;
        return entity;
    }

    public bool Update(Episode entity) => _byId.ContainsKey(entity.Id);

    public bool Delete(int id) => _byId.Remove(id);

    public IReadOnlyList<Episode> GetByMediaItem(int mediaItemId)
        => _byId.Values.Where(e => e.MediaItemId == mediaItemId).ToList();

    public IReadOnlyList<Episode> GetUnwatchedByMediaItem(int mediaItemId)
        => GetByMediaItem(mediaItemId);
}

/// <summary>In-memory <see cref="IMediaItemRepository"/>.</summary>
internal sealed class FakeMediaItemRepository : IMediaItemRepository
{
    private readonly Dictionary<int, MediaItem> _byId = new();

    public void Add(MediaItem item) => _byId[item.Id] = item;

    public IReadOnlyList<MediaItem> GetAll() => _byId.Values.ToList();

    public MediaItem? GetById(int id) => _byId.GetValueOrDefault(id);

    public MediaItem Insert(MediaItem entity)
    {
        _byId[entity.Id] = entity;
        return entity;
    }

    public bool Update(MediaItem entity) => _byId.ContainsKey(entity.Id);

    public bool Delete(int id) => _byId.Remove(id);

    public IReadOnlyList<MediaItem> GetByCategory(int categoryId)
        => _byId.Values.Where(i => i.CategoryId == categoryId).ToList();
}

/// <summary>Recording <see cref="IWatchStateService"/> fake (no database).</summary>
internal sealed class FakeWatchStateService : IWatchStateService
{
    public List<(int EpisodeId, double PositionSec, double DurationSec)> SaveProgressCalls { get; } = new();

    public List<(int EpisodeId, double PositionSec, double DurationSec)> CheckAndMarkCalls { get; } = new();

    public List<int> ToggleCalls { get; } = new();

    public List<(int EpisodeId, bool Watched)> MarkCalls { get; } = new();

    public Task SaveProgressAsync(int episodeId, double positionSec, double durationSec)
    {
        SaveProgressCalls.Add((episodeId, positionSec, durationSec));
        return Task.CompletedTask;
    }

    public Task CheckAndMarkWatchedAsync(int episodeId, double positionSec, double durationSec)
    {
        CheckAndMarkCalls.Add((episodeId, positionSec, durationSec));
        return Task.CompletedTask;
    }

    public Task ToggleWatchedAsync(int episodeId)
    {
        ToggleCalls.Add(episodeId);
        return Task.CompletedTask;
    }

    public Task MarkWatchedAsync(int episodeId, bool watched)
    {
        MarkCalls.Add((episodeId, watched));
        return Task.CompletedTask;
    }

    public Task<WatchState?> GetStateAsync(int episodeId) => Task.FromResult<WatchState?>(null);

    public Task<List<Episode>> GetContinueWatchingAsync() => Task.FromResult(new List<Episode>());

    public Task<bool> ShouldOfferContinueAsync(int episodeId) => Task.FromResult(false);
}

/// <summary>In-memory <see cref="IWatchStateRepository"/>.</summary>
internal sealed class FakeWatchStateRepository : IWatchStateRepository
{
    private readonly Dictionary<int, WatchState> _byId = new();

    public void Add(WatchState state) => _byId[state.Id] = state;

    public IReadOnlyList<WatchState> GetAll() => _byId.Values.ToList();

    public WatchState? GetById(int id) => _byId.GetValueOrDefault(id);

    public WatchState Insert(WatchState entity)
    {
        _byId[entity.Id] = entity;
        return entity;
    }

    public bool Update(WatchState entity) => _byId.ContainsKey(entity.Id);

    public bool Delete(int id) => _byId.Remove(id);

    public WatchState? GetByEpisodeId(int episodeId)
        => _byId.Values.FirstOrDefault(s => s.EpisodeId == episodeId);

    public IReadOnlyList<WatchState> GetInProgress()
        => _byId.Values.Where(s => !s.Watched && s.ProgressPct > 0d).ToList();
}

/// <summary>In-memory <see cref="IAppSettingsRepository"/> (records writes).</summary>
internal sealed class FakeAppSettingsRepository : IAppSettingsRepository
{
    private readonly Dictionary<string, string> _values = new();

    public FakeAppSettingsRepository()
    {
    }

    public FakeAppSettingsRepository(IDictionary<string, string> seed)
    {
        foreach (var kv in seed)
        {
            _values[kv.Key] = kv.Value;
        }
    }

    public List<(string Key, string Value)> SetCalls { get; } = new();

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        _values[key] = value;
        SetCalls.Add((key, value));
    }

    public IReadOnlyDictionary<string, string> GetAll() => _values;
}

/// <summary>Recording <see cref="IThemeService"/> fake (no polling/persistence).</summary>
internal sealed class FakeThemeService : IThemeService
{
    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public AppTheme Override { get; private set; } = AppTheme.System;

    public List<AppTheme> OverrideCalls { get; } = new();

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public event EventHandler<AppTheme>? ThemeChanged;

    public void SetOverride(AppTheme theme)
    {
        Override = theme;
        OverrideCalls.Add(theme);
        ThemeChanged?.Invoke(this, theme);
    }

    public void Start() => StartCount++;

    public void Stop() => StopCount++;
}

/// <summary>Recording <see cref="ILibraryScanner"/> fake (no filesystem scan).</summary>
internal sealed class FakeLibraryScanner : ILibraryScanner
{
    public List<string?> ScanCalls { get; } = new();

    public Exception? ThrowOnScan { get; set; }

    public Task<LibraryScanSummary> ScanAsync(
        string? rootFolder = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ScanCalls.Add(rootFolder);
        if (ThrowOnScan is not null)
        {
            throw ThrowOnScan;
        }

        return Task.FromResult(new LibraryScanSummary());
    }
}

/// <summary>Scriptable <see cref="IFolderPicker"/> fake (no Win32 dialog).</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Result { get; set; }

    public List<string> Titles { get; } = new();

    public string? PickFolder(string title)
    {
        Titles.Add(title);
        return Result;
    }
}

/// <summary>Recording <see cref="ISlidingWindowService"/> fake (ST-19).</summary>
internal sealed class FakeSlidingWindowService : ISlidingWindowService
{
    public List<(int MediaItemId, ProcessProfile Profile)> StartCalls { get; } = new();

    public List<int> StopCalls { get; } = new();

    public List<int> WatchedCalls { get; } = new();

    public int? ActiveMediaItemId { get; set; }

    public ProcessProfile? ActiveProfile { get; set; }

    public List<Episode> WindowEpisodes { get; set; } = new();

    public Task StartWindowAsync(int mediaItemId, ProcessProfile profile)
    {
        StartCalls.Add((mediaItemId, profile));
        ActiveMediaItemId = mediaItemId;
        ActiveProfile = profile;
        return Task.CompletedTask;
    }

    public Task StopWindowAsync(int mediaItemId)
    {
        StopCalls.Add(mediaItemId);
        if (ActiveMediaItemId == mediaItemId)
        {
            ActiveMediaItemId = null;
            ActiveProfile = null;
            WindowEpisodes = new List<Episode>();
        }

        return Task.CompletedTask;
    }

    public Task OnEpisodeWatchedAsync(int episodeId)
    {
        WatchedCalls.Add(episodeId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recording <see cref="IProcessingQueueService"/> fake (ST-19). Events are
/// raised explicitly by the tests; no background worker.
/// </summary>
internal sealed class FakeProcessingQueueService : IProcessingQueueService
{
    public List<(List<int> EpisodeIds, ProcessProfile Profile)> EnqueueCalls { get; } = new();

    public int CancelCurrentCount { get; private set; }

    public int ClearQueueCount { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public ProcessJob? CurrentJob { get; set; }

    public List<ProcessJob> QueuedJobs { get; set; } = new();

    public event EventHandler<ProcessJob>? JobStarted;

    public event EventHandler<ProcessJob>? JobCompleted;

    public event EventHandler<ProcessJob>? JobFailed;

    public event EventHandler<CATRA.Core.Processing.PipelineProgress>? ProgressChanged;

    public Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile)
    {
        EnqueueCalls.Add((episodeIds, profile));
        return Task.CompletedTask;
    }

    public Task CancelCurrentAsync()
    {
        CancelCurrentCount++;
        return Task.CompletedTask;
    }

    public Task ClearQueueAsync()
    {
        ClearQueueCount++;
        return Task.CompletedTask;
    }

    public Task StartAsync()
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public void RaiseJobStarted(ProcessJob job) => JobStarted?.Invoke(this, job);

    public void RaiseJobCompleted(ProcessJob job) => JobCompleted?.Invoke(this, job);

    public void RaiseJobFailed(ProcessJob job) => JobFailed?.Invoke(this, job);

    public void RaiseProgressChanged(CATRA.Core.Processing.PipelineProgress sample)
        => ProgressChanged?.Invoke(this, sample);
}

/// <summary>In-memory <see cref="IProcessedFileRepository"/>.</summary>
internal sealed class FakeProcessedFileRepository : IProcessedFileRepository
{
    private readonly Dictionary<int, ProcessedFile> _byId = new();

    public void Add(ProcessedFile file) => _byId[file.Id] = file;

    public IReadOnlyList<ProcessedFile> GetAll() => _byId.Values.ToList();

    public ProcessedFile? GetById(int id) => _byId.GetValueOrDefault(id);

    public ProcessedFile Insert(ProcessedFile entity)
    {
        _byId[entity.Id] = entity;
        return entity;
    }

    public bool Update(ProcessedFile entity) => _byId.ContainsKey(entity.Id);

    public bool Delete(int id) => _byId.Remove(id);

    public ProcessedFile? GetByEpisodeAndProfile(int episodeId, ProcessProfile profile)
        => _byId.Values.FirstOrDefault(f => f.EpisodeId == episodeId && f.Profile == profile);

    public IReadOnlyList<ProcessedFile> GetByEpisode(int episodeId)
        => _byId.Values.Where(f => f.EpisodeId == episodeId).ToList();
}

/// <summary>In-memory <see cref="IProcessJobRepository"/>.</summary>
internal sealed class FakeProcessJobRepository : IProcessJobRepository
{
    private readonly Dictionary<int, ProcessJob> _byId = new();

    public Func<int, IReadOnlyList<int>>? EpisodeIdsByMediaItem { get; set; }

    public void Add(ProcessJob job) => _byId[job.Id] = job;

    public IReadOnlyList<ProcessJob> GetAll() => _byId.Values.ToList();

    public ProcessJob? GetById(int id) => _byId.GetValueOrDefault(id);

    public ProcessJob Insert(ProcessJob entity)
    {
        _byId[entity.Id] = entity;
        return entity;
    }

    public bool Update(ProcessJob entity) => _byId.ContainsKey(entity.Id);

    public bool Delete(int id) => _byId.Remove(id);

    public IReadOnlyList<ProcessJob> GetActiveByMediaItem(int mediaItemId)
    {
        var ids = EpisodeIdsByMediaItem?.Invoke(mediaItemId) ?? new List<int>();
        return _byId.Values
            .Where(j => ids.Contains(j.EpisodeId)
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Processing))
            .ToList();
    }
}

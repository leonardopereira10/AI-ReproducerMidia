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

/// <summary>Recording <see cref="IAppNavigator"/> fake.</summary>
internal sealed class FakeNavigator : IAppNavigator
{
    public int HomeCount { get; private set; }

    public int BackCount { get; private set; }

    public List<int> DetailIds { get; } = new();

    public List<int> PlayerIds { get; } = new();

    public void GoToHome() => HomeCount++;

    public void GoToMediaDetail(int mediaItemId) => DetailIds.Add(mediaItemId);

    public void GoToPlayer(int episodeId) => PlayerIds.Add(episodeId);

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

using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Services.WebControl;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.WebControl;

/// <summary>
/// Unit tests for <see cref="WebControlService"/> (ST-10) using hand-rolled
/// fakes for every dependency (repo convention — see CastingServiceTests).
/// Covers state snapshots, command dispatch, skip-intro guards, episode
/// navigation and casting event propagation.
/// </summary>
public sealed class WebControlServiceTests : IDisposable
{
    private readonly FakeCastingService _casting = new();
    private readonly FakeSlidingWindowService _slidingWindow = new();
    private readonly FakeEpisodeRepository _episodes = new();
    private readonly FakeMediaItemRepository _mediaItems = new();
    private readonly FakeAppSettingsRepository _appSettings = new();
    private readonly FakeThumbnailService _thumbnails = new();

    private readonly WebControlService _sut;

    public WebControlServiceTests()
    {
        _sut = new WebControlService(
            _casting, _slidingWindow, _episodes, _mediaItems, _appSettings, _thumbnails);
    }

    public void Dispose() => _sut.Dispose();

    private static Episode Episode(int id, int mediaItemId = 7, double? duration = 1000, string? title = "Ep Title", string fileName = "s01e01.mkv")
        => new()
        {
            Id = id,
            MediaItemId = mediaItemId,
            DurationSec = duration,
            DisplayTitle = title,
            FileName = fileName,
            FilePath = $"/media/{fileName}",
        };

    private static MediaItem MediaItem(int id, double skipIntroSec = 85)
        => new() { Id = id, Title = "Series", SkipIntroSec = skipIntroSec };

    private void LoadEpisode(int episodeId, MediaItem? mediaItem = null)
    {
        var episode = Episode(episodeId);
        _episodes.Store(episode);
        _mediaItems.Store(mediaItem ?? MediaItem(episode.MediaItemId));
        _sut.SetCurrentEpisode(episodeId);
    }

    // ── GetCurrentState ─────────────────────────────────────────

    [Fact]
    public void GetCurrentState_NoEpisode_ReturnsDefaults()
    {
        // Act
        var state = _sut.GetCurrentState();

        // Assert
        state.Title.Should().BeEmpty();
        state.Volume.Should().Be(100);
        state.Position.Should().Be(0);
        state.Duration.Should().Be(0);
        state.IsPlaying.Should().BeFalse();
        state.IsPaused.Should().BeFalse();
        state.HasNextEpisode.Should().BeFalse();
        state.HasPreviousEpisode.Should().BeFalse();
        state.CanSkipIntro.Should().BeFalse();
        state.Queue.Should().BeEmpty();
        state.CastDeviceName.Should().BeNull();
    }

    [Fact]
    public void SetCurrentEpisode_ValidEpisode_UpdatesState()
    {
        // Arrange
        var episode = Episode(11, title: "Piloto", duration: 300);
        episode.ThumbnailPath = "/thumbs/11.jpg";
        _episodes.Store(episode);
        _mediaItems.Store(MediaItem(episode.MediaItemId));

        // Act
        _sut.SetCurrentEpisode(11);
        var state = _sut.GetCurrentState();

        // Assert
        state.Title.Should().Be("Piloto");
        state.Duration.Should().Be(300);
        state.ThumbnailUrl.Should().Be("/thumbs/11.jpg");
    }

    [Fact]
    public void SetCurrentEpisode_BlankDisplayTitle_FallsBackToFileName()
    {
        // Arrange
        _episodes.Store(Episode(12, title: "  ", fileName: "s01e02.mkv"));
        _mediaItems.Store(MediaItem(7));

        // Act
        _sut.SetCurrentEpisode(12);

        // Assert
        _sut.GetCurrentState().Title.Should().Be("s01e02.mkv");
    }

    [Fact]
    public void SetCurrentEpisode_UnknownEpisode_DoesNotThrowAndClearsTitle()
    {
        // Act
        var act = () => _sut.SetCurrentEpisode(999);

        // Assert
        act.Should().NotThrow();
        _sut.GetCurrentState().Title.Should().BeEmpty();
    }

    [Fact]
    public void SetCurrentEpisode_RaisesStateChanged()
    {
        // Arrange
        WebControlState? received = null;
        _sut.StateChanged += (_, s) => received = s;
        _episodes.Store(Episode(11));
        _mediaItems.Store(MediaItem(7));

        // Act
        _sut.SetCurrentEpisode(11);

        // Assert
        received.Should().NotBeNull();
        received!.Title.Should().Be("Ep Title");
    }

    [Fact]
    public void GetCurrentState_UsesSlidingWindowQueue_AndMarksCurrent()
    {
        // Arrange
        LoadEpisode(11);
        _slidingWindow.WindowEpisodes.AddRange(new[] { Episode(10, title: "A"), Episode(11, title: "B"), Episode(12, title: "C") });

        // Act
        var state = _sut.GetCurrentState();

        // Assert
        state.Queue.Should().HaveCount(3);
        state.Queue.Should().ContainSingle(q => q.IsCurrent).Which.Id.Should().Be(11);
        state.Queue.Select(q => q.Title).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void GetCurrentState_EmptyWindow_FallsBackToCurrentEpisodeOnly()
    {
        // Arrange
        LoadEpisode(11);

        // Act
        var state = _sut.GetCurrentState();

        // Assert
        state.Queue.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WebControlQueueItem(11, "Ep Title", 1000, true));
    }

    [Fact]
    public void GetCurrentState_ReflectsNextAndPreviousAvailability()
    {
        // Arrange
        LoadEpisode(11);
        _episodes.NextEpisode = Episode(12);

        // Act
        var state = _sut.GetCurrentState();

        // Assert
        state.HasNextEpisode.Should().BeTrue();
        state.HasPreviousEpisode.Should().BeFalse();
    }

    // ── HandleCommandAsync — play / pause / seek / volume ───────

    [Fact]
    public async Task HandleCommand_Play_CallsCastingPlay()
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("play"));

        // Assert
        _casting.PlayCount.Should().Be(1);
        _casting.PauseCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleCommand_Pause_CallsCastingPause()
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("pause"));

        // Assert
        _casting.PauseCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleCommand_Seek_CallsCastingSeekWithPosition()
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("seek", Position: 92.5));

        // Assert
        _casting.SeekCalls.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(92.5));
    }

    [Fact]
    public async Task HandleCommand_SeekWithoutPosition_IsNoOp()
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("seek"));

        // Assert
        _casting.SeekCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(42, 42)]
    [InlineData(150, 100)]   // clamped high
    [InlineData(-5, 0)]      // clamped low
    public async Task HandleCommand_Volume_ClampsAndUpdatesState(int level, int expected)
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("volume", Level: level));

        // Assert
        _casting.VolumeCalls.Should().ContainSingle().Which.Should().Be(expected);
        _sut.GetCurrentState().Volume.Should().Be(expected);
    }

    [Fact]
    public async Task HandleCommand_VolumeWithoutLevel_IsNoOp()
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("volume"));

        // Assert
        _casting.VolumeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCommand_UnknownType_IsNoOp()
    {
        // Act
        var act = () => _sut.HandleCommandAsync(new WebControlCommand("rewind"));

        // Assert
        await act.Should().NotThrowAsync();
        _casting.PlayCount.Should().Be(0);
        _casting.SeekCalls.Should().BeEmpty();
    }

    // ── HandleCommandAsync — skipIntro ──────────────────────────

    [Fact]
    public async Task HandleCommand_SkipIntro_SeeksToPositionPlusSkipSec()
    {
        // Arrange — position 50s, duration 1000s, setting skip 100s ⇒ target 150s.
        _appSettings.Set(AppSettingsModel.DefaultSkipIntroSecKey, "100");
        LoadEpisode(11, MediaItem(7));
        _casting.RaisePositionChanged(TimeSpan.FromSeconds(50));

        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("skipIntro"));

        // Assert
        _casting.SeekCalls.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(150));
    }

    [Fact]
    public async Task HandleCommand_SkipIntro_TooCloseToEnd_DoesNotSeek()
    {
        // Arrange — 940 + 100 > 1000 - 30 (end guard).
        _appSettings.Set(AppSettingsModel.DefaultSkipIntroSecKey, "100");
        LoadEpisode(11, MediaItem(7));
        _casting.RaisePositionChanged(TimeSpan.FromSeconds(940));

        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("skipIntro"));

        // Assert
        _casting.SeekCalls.Should().BeEmpty();
    }

    // ── HandleCommandAsync — next / previous episode ────────────

    [Fact]
    public async Task HandleCommand_NextEpisode_SwitchesToNext()
    {
        // Arrange
        LoadEpisode(11);
        var next = Episode(12, title: "Ep 2", duration: 500);
        _episodes.NextEpisode = next;
        _mediaItems.Store(MediaItem(next.MediaItemId));
        WebControlState? broadcast = null;
        _sut.StateChanged += (_, s) => broadcast = s;

        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("nextEpisode"));

        // Assert
        _casting.StopCastingCount.Should().Be(1);
        _casting.StartCastingCalls.Should().BeEmpty(); // no active device ⇒ no resume
        broadcast.Should().NotBeNull();
        broadcast!.Title.Should().Be("Ep 2");
        broadcast.Position.Should().Be(0);
        broadcast.Duration.Should().Be(500);
    }

    [Fact]
    public async Task HandleCommand_NextEpisode_WithActiveDevice_ResumesCasting()
    {
        // Arrange
        LoadEpisode(11);
        var next = Episode(12, title: "Ep 2");
        _episodes.NextEpisode = next;
        _mediaItems.Store(MediaItem(next.MediaItemId));
        _casting.CurrentDevice = new DlnaDeviceInfo { FriendlyName = "TV", Udn = "uuid:1" };

        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("nextEpisode"));

        // Assert
        _casting.StartCastingCalls.Should().ContainSingle()
            .Which.FilePath.Should().Be(next.FilePath);
    }

    [Fact]
    public async Task HandleCommand_NextEpisode_NoNext_IsNoOp()
    {
        // Arrange
        LoadEpisode(11);
        _episodes.NextEpisode = null;

        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("nextEpisode"));

        // Assert
        _casting.StopCastingCount.Should().Be(0);
        _sut.GetCurrentState().Title.Should().Be("Ep Title"); // unchanged
    }

    [Fact]
    public async Task HandleCommand_PreviousEpisode_WithoutCurrentEpisode_IsNoOp()
    {
        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("previousEpisode"));

        // Assert
        _casting.StopCastingCount.Should().Be(0);
        _episodes.PreviousLookups.Should().Be(0);
    }

    [Fact]
    public async Task HandleCommand_PreviousEpisode_SwitchesToPrevious()
    {
        // Arrange
        LoadEpisode(11);
        var prev = Episode(10, title: "Ep 0", duration: 400);
        _episodes.PreviousEpisode = prev;
        _mediaItems.Store(MediaItem(prev.MediaItemId));

        // Act
        await _sut.HandleCommandAsync(new WebControlCommand("previousEpisode"));

        // Assert
        _casting.StopCastingCount.Should().Be(1);
        _sut.GetCurrentState().Title.Should().Be("Ep 0");
    }

    // ── Casting event propagation ───────────────────────────────

    [Fact]
    public void CastingStateChanged_Streaming_SetsIsPlaying()
    {
        // Arrange
        LoadEpisode(11);

        // Act
        _casting.RaiseStateChanged(CastingState.Streaming);

        // Assert
        var state = _sut.GetCurrentState();
        state.IsPlaying.Should().BeTrue();
        state.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void CastingStateChanged_IdleWithEpisode_SetsIsPaused()
    {
        // Arrange
        LoadEpisode(11);
        _casting.RaiseStateChanged(CastingState.Streaming);

        // Act
        _casting.RaiseStateChanged(CastingState.Idle);

        // Assert
        var state = _sut.GetCurrentState();
        state.IsPlaying.Should().BeFalse();
        state.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void CastingPositionChanged_RaisesPositionEvent_AndUpdatesState()
    {
        // Arrange
        LoadEpisode(11);
        (double Position, double Duration)? received = null;
        _sut.PositionChanged += (_, p) => received = p;

        // Act
        _casting.RaisePositionChanged(TimeSpan.FromSeconds(75));

        // Assert
        received.Should().NotBeNull();
        received!.Value.Position.Should().Be(75);
        received.Value.Duration.Should().Be(1000);
        _sut.GetCurrentState().Position.Should().Be(75);
    }

    [Fact]
    public void CastingMediaEnded_ClearsPlayingAndPaused()
    {
        // Arrange
        LoadEpisode(11);
        _casting.RaiseStateChanged(CastingState.Streaming);

        // Act
        _casting.RaiseMediaEnded();

        // Assert
        var state = _sut.GetCurrentState();
        state.IsPlaying.Should().BeFalse();
        state.IsPaused.Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════
    //  Fakes
    // ════════════════════════════════════════════════════════════

    private sealed class FakeCastingService : ICastingService
    {
        public CastingState State { get; set; } = CastingState.Idle;
        public DlnaDeviceInfo? CurrentDevice { get; set; }
        public string? ErrorMessage { get; set; }

        public int PlayCount { get; private set; }
        public int PauseCount { get; private set; }
        public int StopCastingCount { get; private set; }
        public List<TimeSpan> SeekCalls { get; } = [];
        public List<int> VolumeCalls { get; } = [];
        public List<(DlnaDeviceInfo Device, string FilePath, string Title, TimeSpan Duration)> StartCastingCalls { get; } = [];

        public event EventHandler<CastingState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler? MediaEnded;

        public Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync() => Task.FromResult(new List<DlnaDeviceInfo>());

        public Task StartCastingAsync(DlnaDeviceInfo device, string filePath, string title, TimeSpan duration = default)
        {
            StartCastingCalls.Add((device, filePath, title, duration));
            return Task.CompletedTask;
        }

        public Task PlayAsync() { PlayCount++; return Task.CompletedTask; }
        public Task PauseAsync() { PauseCount++; return Task.CompletedTask; }
        public Task StopCastingAsync() { StopCastingCount++; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position) { SeekCalls.Add(position); return Task.CompletedTask; }
        public Task SetVolumeAsync(int volume) { VolumeCalls.Add(volume); return Task.CompletedTask; }
        public Task<TimeSpan> GetPositionAsync() => Task.FromResult(TimeSpan.Zero);

        public void RaiseStateChanged(CastingState state) { State = state; StateChanged?.Invoke(this, state); }
        public void RaisePositionChanged(TimeSpan position) => PositionChanged?.Invoke(this, position);
        public void RaiseMediaEnded() => MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSlidingWindowService : ISlidingWindowService
    {
        public List<Episode> WindowEpisodes { get; } = [];
        public int? ActiveMediaItemId { get; set; }
        public ProcessProfile? ActiveProfile { get; set; }

        public Task StartWindowAsync(int mediaItemId, ProcessProfile profile) => Task.CompletedTask;
        public Task StopWindowAsync(int mediaItemId) => Task.CompletedTask;
        public Task OnEpisodeWatchedAsync(int episodeId) => Task.CompletedTask;
    }

    private sealed class FakeEpisodeRepository : IEpisodeRepository
    {
        private readonly Dictionary<int, Episode> _store = [];
        public Episode? NextEpisode { get; set; }
        public Episode? PreviousEpisode { get; set; }
        public int NextLookups { get; private set; }
        public int PreviousLookups { get; private set; }

        public void Store(Episode episode) => _store[episode.Id] = episode;

        public Episode? GetNextEpisode(int currentEpisodeId) { NextLookups++; return NextEpisode; }
        public Episode? GetPreviousEpisode(int currentEpisodeId) { PreviousLookups++; return PreviousEpisode; }
        public IReadOnlyList<Episode> GetByMediaItem(int mediaItemId) => [];
        public IReadOnlyList<Episode> GetUnwatchedByMediaItem(int mediaItemId) => [];
        public IReadOnlyList<Episode> GetAll() => _store.Values.ToList();
        public Episode? GetById(int id) => _store.GetValueOrDefault(id);
        public Episode Insert(Episode entity) { _store[entity.Id] = entity; return entity; }
        public bool Update(Episode entity) => _store.ContainsKey(entity.Id);
        public bool Delete(int id) => _store.Remove(id);
    }

    private sealed class FakeMediaItemRepository : IMediaItemRepository
    {
        private readonly Dictionary<int, MediaItem> _store = [];

        public void Store(MediaItem item) => _store[item.Id] = item;

        public IReadOnlyList<MediaItem> GetByCategory(int categoryId) => [];
        public IReadOnlyList<MediaItem> GetAll() => _store.Values.ToList();
        public MediaItem? GetById(int id) => _store.GetValueOrDefault(id);
        public MediaItem Insert(MediaItem entity) { _store[entity.Id] = entity; return entity; }
        public bool Update(MediaItem entity) => _store.ContainsKey(entity.Id);
        public bool Delete(int id) => _store.Remove(id);
    }

    private sealed class FakeAppSettingsRepository : IAppSettingsRepository
    {
        private readonly Dictionary<string, string> _store = [];

        public string? Get(string key) => _store.GetValueOrDefault(key);
        public void Set(string key, string value) => _store[key] = value;
        public IReadOnlyDictionary<string, string> GetAll() => _store;
    }

    private sealed class FakeThumbnailService : IThumbnailService
    {
        public Task<string?> GetOrCreateThumbnailAsync(Episode episode) => Task.FromResult<string?>(null);
        public Task<string?> GetOrCreateSeriesCoverAsync(MediaItem mediaItem) => Task.FromResult<string?>(null);
        public Task SetCustomCoverAsync(int mediaItemId, string imagePath) => Task.CompletedTask;
        public Task ClearCacheAsync() => Task.CompletedTask;
    }
}

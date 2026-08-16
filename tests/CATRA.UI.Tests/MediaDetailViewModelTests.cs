using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Models;
using CATRA.UI.Services;
using CATRA.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CATRA.UI.Tests;

/// <summary>
/// Focused tests for <see cref="MediaDetailViewModel"/>. The RF-08 cover flow
/// must route through <see cref="IThumbnailService.SetCustomCoverAsync"/> (which
/// copies the image into the cache and persists <c>MediaItem.CoverPath</c>),
/// NOT through the raw <c>ILibraryService.SetMediaCoverAsync</c> path writer.
/// </summary>
public sealed class MediaDetailViewModelTests
{
    private readonly FakeLibraryService _library = new();
    private readonly FakeWatchStateService _watchState = new();
    private readonly RecordingThumbnailService _thumbnails = new();
    private readonly FakeNavigator _navigator = new();
    private readonly ScriptableDialogService _dialogs = new();
    private readonly FakeSlidingWindowService _window = new();
    private readonly FakeProcessedFileRepository _processedFiles = new();
    private readonly FakeProcessJobRepository _jobs = new();
    private readonly FakeAppSettingsRepository _settings = new();

    private MediaDetailViewModel CreateVm() =>
        new(_library, _watchState, _thumbnails, _navigator, _dialogs, _window, _processedFiles, _jobs, _settings);

    [Fact]
    public async Task SetCover_PickedFile_RoutesThroughThumbnailService_NotRawLibraryPath()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        _dialogs.OpenFileResult = @"C:/pics/cover.png";
        var vm = CreateVm();
        await vm.LoadAsync(7);

        await vm.SetCoverCommand.ExecuteAsync(null);

        _thumbnails.SetCustomCoverCalls.Should().ContainSingle(
            "RF-08: the cover must be copied into the cache via IThumbnailService");
        _thumbnails.SetCustomCoverCalls[0].MediaItemId.Should().Be(7);
        _thumbnails.SetCustomCoverCalls[0].ImagePath.Should().Be(@"C:/pics/cover.png");
        _library.SetMediaCoverCalls.Should().BeEmpty(
            "the VM must no longer write the raw source path through ILibraryService");
    }

    [Fact]
    public async Task SetCover_CancelledPicker_DoesNothing()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        _dialogs.OpenFileResult = null; // user cancelled
        var vm = CreateVm();
        await vm.LoadAsync(7);

        await vm.SetCoverCommand.ExecuteAsync(null);

        _thumbnails.SetCustomCoverCalls.Should().BeEmpty();
        _library.SetMediaCoverCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task StartStopWindow_NoActiveWindow_StartsWindowWithSelectedProfile()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        var vm = CreateVm();
        vm.SelectedProfile = CATRA.Core.Enums.ProcessProfile.Dlna;
        await vm.LoadAsync(7);

        await vm.StartStopWindowCommand.ExecuteAsync(null);

        _window.StartCalls.Should().ContainSingle();
        _window.StartCalls[0].MediaItemId.Should().Be(7);
        _window.StartCalls[0].Profile.Should().Be(CATRA.Core.Enums.ProcessProfile.Dlna);
    }

    [Fact]
    public async Task StartStopWindow_ActiveForThisSeries_StopsWindow()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        _window.ActiveMediaItemId = 7;
        _window.ActiveProfile = CATRA.Core.Enums.ProcessProfile.Local;
        var vm = CreateVm();
        await vm.LoadAsync(7);

        vm.StartStopLabel.Should().Be("⏹ Parar");
        vm.IsWindowActiveForThisSeries.Should().BeTrue();

        await vm.StartStopWindowCommand.ExecuteAsync(null);

        _window.StopCalls.Should().ContainSingle().Which.Should().Be(7);
        _window.StartCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_OtherSeriesActive_SetsWarningFlag()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        _window.ActiveMediaItemId = 99; // outra série
        var vm = CreateVm();

        await vm.LoadAsync(7);

        vm.IsOtherSeriesWindowActive.Should().BeTrue();
        vm.IsWindowActiveForThisSeries.Should().BeFalse();
    }

    [Fact]
    public async Task WindowEstimate_IsSettingsDriven_NotHardcoded()
    {
        // ST-19 follow-up minor: the estimate must read window_size +
        // *_encode_bitrate_kbps from settings (matching ProcessingQueueViewModel),
        // not the old hardcoded Take(5) / 20k window.
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        for (int i = 1; i <= 4; i++)
        {
            _library.Episodes.Add(new EpisodeDetail(
                new Episode { Id = i, MediaItemId = 7, EpisodeNumber = i, FileName = $"ep{i}.mkv", DurationSec = 1000d },
                watchState: null));
        }

        var settings = new FakeAppSettingsRepository(new Dictionary<string, string>
        {
            ["window_size"] = "3",
            ["local_encode_bitrate_kbps"] = "20000",
        });
        var vm = new MediaDetailViewModel(
            _library, _watchState, _thumbnails, _navigator, _dialogs, _window, _processedFiles, _jobs, settings);

        await vm.LoadAsync(7);

        // 3 eps × 1000s × (20000kbps × 1000 / 8) = 7.5e9 bytes → 7.5 GB.
        vm.WindowEstimateLabel.Should().StartWith("Janela: 3 episódios");
        vm.WindowEstimateLabel.Should().EndWith("GB estimados");
    }

    [Fact]
    public async Task OpenQueue_NavigatesToProcessingQueueWithMediaItem()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        var vm = CreateVm();
        await vm.LoadAsync(7);

        vm.OpenQueueCommand.Execute(null);

        _navigator.QueueIds.Should().ContainSingle().Which.Should().Be(7);
    }

    [Fact]
    public async Task Load_ReturningFromPlayer_ReflectsUpdatedWatchState()
    {
        // Bug fix regression: after watching an episode to the end and going
        // back to the detail screen, the reload must move the episode from the
        // unwatched grid to the watched grid (the view re-triggers LoadAsync
        // when the kept-alive page is shown again).
        var episode = new Episode { Id = 1, MediaItemId = 7, EpisodeNumber = 1, FileName = "ep1.mkv", DurationSec = 1000d };
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        _library.Episodes.Add(new EpisodeDetail(episode, watchState: null));
        var vm = CreateVm();
        await vm.LoadAsync(7);

        vm.UnwatchedEpisodes.Should().ContainSingle();
        vm.WatchedEpisodes.Should().BeEmpty();

        // Watch state persisted by the player (RN-02 crossed the threshold).
        _library.Episodes.Clear();
        _library.Episodes.Add(new EpisodeDetail(
            episode,
            new CATRA.Core.Models.WatchState { EpisodeId = 1, Watched = true, ProgressPct = 100d }));

        await vm.LoadAsync(7);

        vm.WatchedEpisodes.Should().ContainSingle();
        vm.UnwatchedEpisodes.Should().BeEmpty();
        vm.ProgressLine.Should().Be("1 eps | 1 assistidos");
        vm.HasWatched.Should().BeTrue();
        vm.HasUnwatched.Should().BeFalse();
    }

    // ── fakes ──────────────────────────────────────────────────────────────

    /// <summary>Recording <see cref="IThumbnailService"/> fake (no filesystem).</summary>
    private sealed class RecordingThumbnailService : IThumbnailService
    {
        public List<(int MediaItemId, string ImagePath)> SetCustomCoverCalls { get; } = new();

        public Task<string?> GetOrCreateThumbnailAsync(Episode episode) => Task.FromResult<string?>(null);

        public Task<string?> GetOrCreateSeriesCoverAsync(MediaItem mediaItem) => Task.FromResult<string?>(null);

        public Task SetCustomCoverAsync(int mediaItemId, string imagePath)
        {
            SetCustomCoverCalls.Add((mediaItemId, imagePath));
            return Task.CompletedTask;
        }

        public Task ClearCacheAsync() => Task.CompletedTask;
    }

    /// <summary><see cref="IDialogService"/> fake with a scriptable file picker.</summary>
    private sealed class ScriptableDialogService : IDialogService
    {
        public string? OpenFileResult { get; set; }

        public List<(string Title, string Message)> Messages { get; } = new();

        public void ShowMessage(string title, string message) => Messages.Add((title, message));

        public bool Confirm(string title, string message, string acceptText, string cancelText) => false;

        public string? Prompt(string title, string label, string initialValue) => null;

        public string? OpenFile(string title, string filter) => OpenFileResult;
    }

    /// <summary>Minimal <see cref="ILibraryService"/> fake recording cover writes.</summary>
    private sealed class FakeLibraryService : ILibraryService
    {
        public MediaItem? MediaItem { get; set; }

        public List<EpisodeDetail> Episodes { get; } = new();

        public List<(int MediaItemId, string? CoverPath)> SetMediaCoverCalls { get; } = new();

        public event EventHandler<LibraryScanSummary>? LibraryUpdated;

        public Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync() =>
            Task.FromResult<IReadOnlyList<CategorySummary>>(new List<CategorySummary>());

        public Task<MediaItem?> GetMediaItemAsync(int mediaItemId) => Task.FromResult(MediaItem);

        public Task<IReadOnlyList<MediaItemSummary>> GetMediaItemsAsync(int? categoryId = null) =>
            Task.FromResult<IReadOnlyList<MediaItemSummary>>(new List<MediaItemSummary>());

        public Task<IReadOnlyList<EpisodeDetail>> GetEpisodesAsync(int mediaItemId) =>
            Task.FromResult<IReadOnlyList<EpisodeDetail>>(Episodes);

        public Task ToggleWatchedAsync(int episodeId) => Task.CompletedTask;

        public Task RenameEpisodeAsync(int episodeId, string newDisplayTitle) => Task.CompletedTask;

        public Task SetMediaCoverAsync(int mediaItemId, string? coverPath)
        {
            SetMediaCoverCalls.Add((mediaItemId, coverPath));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MediaItemSummary>> GetContinueWatchingAsync() =>
            Task.FromResult<IReadOnlyList<MediaItemSummary>>(new List<MediaItemSummary>());

        public Task<LibraryScanSummary> RefreshAsync(CancellationToken cancellationToken = default)
        {
            LibraryUpdated?.Invoke(this, new LibraryScanSummary());
            return Task.FromResult(new LibraryScanSummary());
        }
    }
}

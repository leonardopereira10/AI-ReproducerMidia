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

    private MediaDetailViewModel CreateVm() =>
        new(_library, _watchState, _thumbnails, _navigator, _dialogs, _window, _processedFiles, _jobs);

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
    public async Task OpenQueue_NavigatesToProcessingQueueWithMediaItem()
    {
        _library.MediaItem = new MediaItem { Id = 7, Title = "Série" };
        var vm = CreateVm();
        await vm.LoadAsync(7);

        vm.OpenQueueCommand.Execute(null);

        _navigator.QueueIds.Should().ContainSingle().Which.Should().Be(7);
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

        public List<(int MediaItemId, string? CoverPath)> SetMediaCoverCalls { get; } = new();

        public event EventHandler<LibraryScanSummary>? LibraryUpdated;

        public Task<IReadOnlyList<CategorySummary>> GetCategoriesAsync() =>
            Task.FromResult<IReadOnlyList<CategorySummary>>(new List<CategorySummary>());

        public Task<MediaItem?> GetMediaItemAsync(int mediaItemId) => Task.FromResult(MediaItem);

        public Task<IReadOnlyList<MediaItemSummary>> GetMediaItemsAsync(int? categoryId = null) =>
            Task.FromResult<IReadOnlyList<MediaItemSummary>>(new List<MediaItemSummary>());

        public Task<IReadOnlyList<EpisodeDetail>> GetEpisodesAsync(int mediaItemId) =>
            Task.FromResult<IReadOnlyList<EpisodeDetail>>(new List<EpisodeDetail>());

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

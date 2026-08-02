using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Models;
using CATRA.Core.Processing;
using CATRA.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CATRA.UI.Tests;

/// <summary>
/// Focused tests for <see cref="ProcessingQueueViewModel"/> (ST-19). The queue
/// service events arrive on a background thread in production; here
/// <see cref="System.Threading.SynchronizationContext.Current"/> is null so the
/// view model marshals inline and the bindables update synchronously.
/// </summary>
public sealed class ProcessingQueueViewModelTests
{
    private readonly FakeProcessingQueueService _queue = new();
    private readonly FakeSlidingWindowService _window = new();
    private readonly FakeProcessedFileRepository _processedFiles = new();
    private readonly FakeAppSettingsRepository _settings = new();
    private readonly StubLibraryService _library = new();
    private readonly FakeNavigator _navigator = new();

    public ProcessingQueueViewModelTests()
    {
        // xUnit installs a SynchronizationContext; the VM marshals queue events
        // through it. Null it so posted callbacks run inline and bindables update
        // synchronously (same pattern as PlayerViewModelTests).
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private ProcessingQueueViewModel CreateVm()
    {
        // Must run here (not just the ctor): xUnit installs an async
        // SynchronizationContext around each test method, so null it right before
        // the VM captures it to make posted callbacks run inline.
        SynchronizationContext.SetSynchronizationContext(null);
        return new ProcessingQueueViewModel(_queue, _window, _processedFiles, _settings, _library, _navigator);
    }

    private static Episode Ep(int id, int number, double? duration = 1000d, string? hash = "h") =>
        new() { Id = id, MediaItemId = 1, EpisodeNumber = number, FileName = $"ep{number}.mkv", DurationSec = duration, FileHash = hash };

    [Fact]
    public async Task ProgressChanged_UpdatesLiveBindables()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Local;
        _window.WindowEpisodes = new List<Episode> { Ep(10, 1) };
        _library.MediaItem = new MediaItem { Id = 1, Title = "Série" };
        _queue.CurrentJob = new ProcessJob { Id = 1, EpisodeId = 10, Status = JobStatus.Processing, ProgressPct = 10 };
        var vm = CreateVm();
        await vm.LoadAsync(null);

        _queue.RaiseProgressChanged(new PipelineProgress(
            EpisodeIndex: 0,
            EpisodeCount: 1,
            CurrentStep: PipelineStep.Upscale,
            StepProgressPct: 50,
            OverallPct: 58,
            Elapsed: TimeSpan.FromMinutes(2),
            Eta: TimeSpan.FromMinutes(3)));

        vm.CurrentProgressPct.Should().Be(58);
        vm.CurrentStepLabel.Should().Be("Upscale (FSR4)");
        vm.CurrentProgressText.Should().Be("58% — Upscale (FSR4)");
        vm.EtaDisplay.Should().Be("~3 min");
    }

    [Fact]
    public async Task CancelCommand_CallsCancelCurrentAsync()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Local;
        _window.WindowEpisodes = new List<Episode> { Ep(10, 1) };
        var vm = CreateVm();
        await vm.LoadAsync(null);

        await vm.CancelCommand.ExecuteAsync(null);

        _queue.CancelCurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task Load_ComputesDiskUsedAndEstimated()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Local;
        _window.WindowEpisodes = new List<Episode> { Ep(10, 1), Ep(11, 2) };
        _processedFiles.Add(new ProcessedFile { Id = 1, EpisodeId = 10, Profile = ProcessProfile.Local, FileSizeBytes = 3_000_000_000 });
        var vm = CreateVm();

        await vm.LoadAsync(null);

        vm.DiskUsedBytes.Should().Be(3_000_000_000);
        // 2 eps × 1000s × 20000kbps × 1000 / 8 = 5.0 GB.
        vm.DiskEstimatedBytes.Should().Be(5_000_000_000);
        vm.DiskUsedLabel.Should().Be("3.0 GB");
        vm.DiskEstimatedLabel.Should().Be("5.0 GB");
        vm.DiskProgressPct.Should().BeApproximately(60d, 0.01d);
    }

    [Fact]
    public async Task Load_MapsWindowEpisodeStatuses()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Local;
        _window.WindowEpisodes = new List<Episode>
        {
            Ep(10, 1, hash: "h"),              // ready (processed, hash matches)
            Ep(11, 2, hash: "h"),              // queued (active job)
            Ep(12, 3, hash: "changed"),        // stale (processed, hash differs)
            Ep(13, 4, hash: "h"),              // original (nothing)
        };
        _processedFiles.Add(new ProcessedFile { Id = 1, EpisodeId = 10, Profile = ProcessProfile.Local, SourceHash = "h", FileSizeBytes = 1 });
        _processedFiles.Add(new ProcessedFile { Id = 2, EpisodeId = 12, Profile = ProcessProfile.Local, SourceHash = "old", FileSizeBytes = 1 });
        _queue.QueuedJobs = new List<ProcessJob> { new() { Id = 5, EpisodeId = 11, Status = JobStatus.Queued } };
        var vm = CreateVm();

        await vm.LoadAsync(null);

        vm.WindowEpisodes.Should().HaveCount(4);
        vm.WindowEpisodes[0].Status.Should().Be(EpisodeProcessStatus.Ready);
        vm.WindowEpisodes[1].Status.Should().Be(EpisodeProcessStatus.Queued);
        vm.WindowEpisodes[2].Status.Should().Be(EpisodeProcessStatus.Stale);
        vm.WindowEpisodes[3].Status.Should().Be(EpisodeProcessStatus.Original);
    }

    [Fact]
    public async Task JobStarted_SetsCurrentJobLabels()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Local;
        _window.WindowEpisodes = new List<Episode> { Ep(10, 3, hash: "h") };
        var vm = CreateVm();
        await vm.LoadAsync(null);
        vm.HasCurrentJob.Should().BeFalse();

        var job = new ProcessJob { Id = 1, EpisodeId = 10, Status = JobStatus.Processing, ProgressPct = 5, CurrentStep = ProcessStep.Decode };
        _queue.CurrentJob = job;
        _queue.RaiseJobStarted(job);

        vm.HasCurrentJob.Should().BeTrue();
        vm.CurrentJobEpisodeLabel.Should().Be("EP03");
    }

    [Fact]
    public async Task JobCompleted_ClearsCurrentJobAndRefreshesDisk()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Local;
        _window.WindowEpisodes = new List<Episode> { Ep(10, 1) };
        var vm = CreateVm();
        await vm.LoadAsync(null);
        vm.DiskUsedBytes.Should().Be(0);

        // A processed file appears (job finished) and the current job clears.
        _processedFiles.Add(new ProcessedFile { Id = 1, EpisodeId = 10, Profile = ProcessProfile.Local, FileSizeBytes = 2_000_000_000, SourceHash = "h" });
        _queue.CurrentJob = null;
        _queue.RaiseJobCompleted(new ProcessJob { Id = 1, EpisodeId = 10, Status = JobStatus.Completed });

        vm.HasCurrentJob.Should().BeFalse();
        vm.DiskUsedBytes.Should().Be(2_000_000_000);
        vm.WindowEpisodes[0].Status.Should().Be(EpisodeProcessStatus.Ready);
    }

    [Fact]
    public async Task Load_NoActiveWindow_ShowsEmptyState()
    {
        _window.ActiveMediaItemId = null;
        var vm = CreateVm();

        await vm.LoadAsync(null);

        vm.SeriesTitle.Should().Be("Nenhuma janela ativa");
        vm.WindowEpisodes.Should().BeEmpty();
        vm.HasCurrentJob.Should().BeFalse();
    }

    [Fact]
    public async Task ActiveProfileLabel_UsesSettingsResolutionAndFps()
    {
        _window.ActiveMediaItemId = 1;
        _window.ActiveProfile = ProcessProfile.Dlna;
        _window.WindowEpisodes = new List<Episode> { Ep(10, 1) };
        var vm = CreateVm();

        await vm.LoadAsync(null);

        vm.ActiveProfileLabel.Should().Be("DLNA (2160p 55fps)");
    }

    /// <summary>Minimal <see cref="ILibraryService"/> returning one media item.</summary>
    private sealed class StubLibraryService : ILibraryService
    {
        public MediaItem? MediaItem { get; set; }

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

        public Task SetMediaCoverAsync(int mediaItemId, string? coverPath) => Task.CompletedTask;

        public Task<IReadOnlyList<MediaItemSummary>> GetContinueWatchingAsync() =>
            Task.FromResult<IReadOnlyList<MediaItemSummary>>(new List<MediaItemSummary>());

        public Task<LibraryScanSummary> RefreshAsync(CancellationToken cancellationToken = default)
        {
            LibraryUpdated?.Invoke(this, new LibraryScanSummary());
            return Task.FromResult(new LibraryScanSummary());
        }
    }
}

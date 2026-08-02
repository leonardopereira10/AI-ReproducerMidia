using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Processing;

/// <summary>
/// ST-18 / RN-10 tests for <see cref="SlidingWindowService"/>: real repositories over a
/// temporary SQLite database, a recording fake queue (no worker) and a controllable
/// in-use probe. Covers window sizing, rotation on watch, series switching, and the
/// "never delete an in-use file" guarantees (proactive probe + reactive file lock).
/// </summary>
public sealed class SlidingWindowServiceTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly DatabaseConnection _database;
    private readonly EpisodeRepository _episodes;
    private readonly ProcessedFileRepository _processedFiles;
    private readonly WatchStateRepository _watchStates;
    private readonly AppSettingsRepository _settings;
    private readonly FakeProcessingQueue _queue;
    private readonly FakeProcessedFileUsage _usage;
    private readonly SlidingWindowService _service;

    public SlidingWindowServiceTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-st18-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _episodes = new EpisodeRepository(_database);
        _processedFiles = new ProcessedFileRepository(_database);
        _watchStates = new WatchStateRepository(_database);
        _settings = new AppSettingsRepository(_database);

        _queue = new FakeProcessingQueue();
        _usage = new FakeProcessedFileUsage();

        // Shrink the delete-retry delay so the locked-file test stays fast.
        _service = new SlidingWindowService(
            _episodes, _processedFiles, _queue, _settings, _usage,
            maxDeleteRetries: 3, deleteRetryDelay: TimeSpan.FromMilliseconds(10));
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: WAL files may linger briefly on Windows.
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private Episode SeedEpisode(int mediaItemId, int number) =>
        _episodes.Insert(new Episode
        {
            MediaItemId = mediaItemId,
            EpisodeNumber = number,
            FileName = $"s{mediaItemId}-ep{number}.mkv",
            FilePath = Path.Combine(_workDirectory, $"s{mediaItemId}-ep{number}.mkv"),
        });

    private List<Episode> SeedSeries(int mediaItemId, int count) =>
        Enumerable.Range(1, count).Select(n => SeedEpisode(mediaItemId, n)).ToList();

    private void MarkWatched(int episodeId) =>
        _watchStates.Insert(new WatchState { EpisodeId = episodeId, Watched = true, ProgressPct = 100d });

    /// <summary>Creates a real processed file on disk plus its DB record.</summary>
    private ProcessedFile SeedProcessedFile(int episodeId, ProcessProfile profile)
    {
        string path = Path.Combine(_workDirectory, "processed", $"{episodeId}_{profile}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "processed-bytes");
        return _processedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episodeId,
            Profile = profile,
            FilePath = path,
            FileSizeBytes = 15,
            TargetFps = 135,
            TargetWidth = 1920,
            TargetHeight = 1080,
        });
    }

    // ── contract ───────────────────────────────────────────────────────────

    [Fact]
    public void Service_ImplementsISlidingWindowService() =>
        typeof(SlidingWindowService).Should().Implement<ISlidingWindowService>();

    // ── StartWindow: sizing ────────────────────────────────────────────────

    [Fact]
    public async Task StartWindow_EnqueuesFirstFiveUnwatched()
    {
        var series = SeedSeries(mediaItemId: 1, count: 8);

        await _service.StartWindowAsync(1, ProcessProfile.Local);

        _queue.EnqueueCalls.Should().HaveCount(1);
        _queue.EnqueueCalls[0].Profile.Should().Be(ProcessProfile.Local);
        _queue.EnqueueCalls[0].Ids.Should().Equal(series.Take(5).Select(e => e.Id));
        _service.WindowEpisodes.Select(e => e.Id).Should().Equal(series.Take(5).Select(e => e.Id));
        _service.ActiveMediaItemId.Should().Be(1);
        _service.ActiveProfile.Should().Be(ProcessProfile.Local);
    }

    [Fact]
    public async Task StartWindow_SkipsWatchedEpisodes()
    {
        var series = SeedSeries(mediaItemId: 1, count: 8);
        MarkWatched(series[0].Id); // EP1 watched → window starts at EP2
        MarkWatched(series[1].Id); // EP2 watched → window starts at EP3

        await _service.StartWindowAsync(1, ProcessProfile.Local);

        _queue.EnqueueCalls[0].Ids.Should().Equal(series.Skip(2).Take(5).Select(e => e.Id));
    }

    [Fact]
    public async Task StartWindow_FewerThanWindow_EnqueuesAll()
    {
        var series = SeedSeries(mediaItemId: 1, count: 3);

        await _service.StartWindowAsync(1, ProcessProfile.Local);

        _queue.EnqueueCalls[0].Ids.Should().Equal(series.Select(e => e.Id));
        _service.WindowEpisodes.Should().HaveCount(3);
    }

    [Fact]
    public async Task StartWindow_RespectsWindowSizeSetting()
    {
        _settings.Set(SlidingWindowService.WindowSizeKey, "2");
        var series = SeedSeries(mediaItemId: 1, count: 6);

        await _service.StartWindowAsync(1, ProcessProfile.Local);

        _queue.EnqueueCalls[0].Ids.Should().Equal(series.Take(2).Select(e => e.Id));
        _service.WindowEpisodes.Should().HaveCount(2);
    }

    // ── rotation on watch ──────────────────────────────────────────────────

    [Fact]
    public async Task OnEpisodeWatched_DeletesProcessed_AndEnqueuesNext_KeepsFive()
    {
        var series = SeedSeries(mediaItemId: 1, count: 8);
        await _service.StartWindowAsync(1, ProcessProfile.Local);
        ProcessedFile processed = SeedProcessedFile(series[0].Id, ProcessProfile.Local);

        MarkWatched(series[0].Id); // EP1 watched
        await _service.OnEpisodeWatchedAsync(series[0].Id);

        // Processed file reclaimed (disk + DB).
        File.Exists(processed.FilePath).Should().BeFalse();
        _processedFiles.GetByEpisodeAndProfile(series[0].Id, ProcessProfile.Local).Should().BeNull();

        // Next unwatched outside the window (EP6) enqueued.
        _queue.EnqueueCalls.Should().HaveCount(2);
        _queue.EnqueueCalls[1].Ids.Should().Equal(new[] { series[5].Id });

        // Window slid: EP1 out, EP6 in, still 5.
        _service.WindowEpisodes.Select(e => e.Id)
            .Should().Equal(series.Skip(1).Take(5).Select(e => e.Id));
    }

    [Fact]
    public async Task OnEpisodeWatched_NoNextEpisode_ShrinksWindow_NoEnqueue()
    {
        var series = SeedSeries(mediaItemId: 1, count: 5);
        await _service.StartWindowAsync(1, ProcessProfile.Local);

        MarkWatched(series[0].Id);
        await _service.OnEpisodeWatchedAsync(series[0].Id);

        _queue.EnqueueCalls.Should().HaveCount(1, "no successor exists to enqueue");
        _service.WindowEpisodes.Should().HaveCount(4);
        _service.WindowEpisodes.Select(e => e.Id).Should().NotContain(series[0].Id);
    }

    [Fact]
    public async Task OnEpisodeWatched_OtherSeries_IsIgnored()
    {
        SeedSeries(mediaItemId: 1, count: 6);
        var other = SeedEpisode(mediaItemId: 2, number: 1);
        await _service.StartWindowAsync(1, ProcessProfile.Local);

        await _service.OnEpisodeWatchedAsync(other.Id);

        _queue.EnqueueCalls.Should().HaveCount(1, "only the StartWindow enqueue happened");
        _service.WindowEpisodes.Should().HaveCount(5);
    }

    [Fact]
    public async Task OnEpisodeWatched_NoActiveWindow_IsNoOp()
    {
        var ep = SeedEpisode(mediaItemId: 1, number: 1);

        await _service.OnEpisodeWatchedAsync(ep.Id);

        _queue.EnqueueCalls.Should().BeEmpty();
    }

    // ── series switching (uma série por vez) ───────────────────────────────

    [Fact]
    public async Task StartWindow_SwitchSeries_CancelsPreviousAndClearsQueue()
    {
        var seriesA = SeedSeries(mediaItemId: 1, count: 6);
        var seriesB = SeedSeries(mediaItemId: 2, count: 6);

        await _service.StartWindowAsync(1, ProcessProfile.Local);
        await _service.StartWindowAsync(2, ProcessProfile.Dlna);

        _queue.CancelCurrentCount.Should().Be(1, "the active job of series A is cancelled");
        _queue.ClearQueueCount.Should().Be(1, "the queued jobs of series A are cleared");
        _service.ActiveMediaItemId.Should().Be(2);
        _service.ActiveProfile.Should().Be(ProcessProfile.Dlna);
        _service.WindowEpisodes.Select(e => e.Id).Should().Equal(seriesB.Take(5).Select(e => e.Id));

        _queue.EnqueueCalls.Should().HaveCount(2);
        _queue.EnqueueCalls[0].Ids.Should().Equal(seriesA.Take(5).Select(e => e.Id));
        _queue.EnqueueCalls[1].Ids.Should().Equal(seriesB.Take(5).Select(e => e.Id));
    }

    [Fact]
    public async Task StartWindow_SameSeries_DoesNotCancel()
    {
        SeedSeries(mediaItemId: 1, count: 6);

        await _service.StartWindowAsync(1, ProcessProfile.Local);
        await _service.StartWindowAsync(1, ProcessProfile.Local);

        _queue.CancelCurrentCount.Should().Be(0);
        _queue.ClearQueueCount.Should().Be(0);
    }

    // ── StopWindow ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StopWindow_ClearsQueueAndResetsState()
    {
        SeedSeries(mediaItemId: 1, count: 6);
        await _service.StartWindowAsync(1, ProcessProfile.Local);

        await _service.StopWindowAsync(1);

        _queue.CancelCurrentCount.Should().Be(1);
        _queue.ClearQueueCount.Should().Be(1);
        _service.ActiveMediaItemId.Should().BeNull();
        _service.ActiveProfile.Should().BeNull();
        _service.WindowEpisodes.Should().BeEmpty();
    }

    [Fact]
    public async Task StopWindow_NonActiveSeries_IsNoOp()
    {
        SeedSeries(mediaItemId: 1, count: 6);
        await _service.StartWindowAsync(1, ProcessProfile.Local);

        await _service.StopWindowAsync(2);

        _queue.CancelCurrentCount.Should().Be(0, "another series' window must not be touched");
        _queue.ClearQueueCount.Should().Be(0);
        _service.ActiveMediaItemId.Should().Be(1);
    }

    // ── never delete an in-use file ────────────────────────────────────────

    [Fact]
    public async Task OnEpisodeWatched_FileReportedInUse_IsNotDeleted()
    {
        var series = SeedSeries(mediaItemId: 1, count: 8);
        await _service.StartWindowAsync(1, ProcessProfile.Local);
        ProcessedFile processed = SeedProcessedFile(series[0].Id, ProcessProfile.Local);
        _usage.SetInUse(processed.FilePath); // proactive probe: playing/streaming

        MarkWatched(series[0].Id);
        await _service.OnEpisodeWatchedAsync(series[0].Id);

        File.Exists(processed.FilePath).Should().BeTrue("an in-use file is never deleted");
        _processedFiles.GetByEpisodeAndProfile(series[0].Id, ProcessProfile.Local)
            .Should().NotBeNull("its DB record is kept so the file is not orphaned");

        // Rotation still happens for the successor.
        _queue.EnqueueCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task OnEpisodeWatched_FileLockedByPlayer_IsNotDeleted()
    {
        var series = SeedSeries(mediaItemId: 1, count: 8);
        await _service.StartWindowAsync(1, ProcessProfile.Local);
        ProcessedFile processed = SeedProcessedFile(series[0].Id, ProcessProfile.Local);

        // Hold an exclusive-ish handle (no FileShare.Delete) so File.Delete throws
        // IOException — simulating the player/streamer holding the file open.
        await using (var lockStream = new FileStream(
                         processed.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            MarkWatched(series[0].Id);
            await _service.OnEpisodeWatchedAsync(series[0].Id);

            File.Exists(processed.FilePath).Should().BeTrue("a locked file survives the delete retries");
        }

        _processedFiles.GetByEpisodeAndProfile(series[0].Id, ProcessProfile.Local)
            .Should().NotBeNull("the record is kept when deletion fails");
    }

    [Fact]
    public async Task OnEpisodeWatched_FileNotInUse_IsDeleted()
    {
        var series = SeedSeries(mediaItemId: 1, count: 8);
        await _service.StartWindowAsync(1, ProcessProfile.Local);
        ProcessedFile processed = SeedProcessedFile(series[0].Id, ProcessProfile.Local);

        MarkWatched(series[0].Id);
        await _service.OnEpisodeWatchedAsync(series[0].Id);

        File.Exists(processed.FilePath).Should().BeFalse();
        _processedFiles.GetByEpisodeAndProfile(series[0].Id, ProcessProfile.Local).Should().BeNull();
    }
}

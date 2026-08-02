using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Core.Processing;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Processing;

/// <summary>
/// ST-18 tests for <see cref="ProcessingQueueService"/>: real repositories over a
/// temporary SQLite database plus a fake pipeline (no GPU). The background worker is
/// synchronised through events / <see cref="TaskCompletionSource"/> — no sleeps — so the
/// suite is deterministic.
/// </summary>
public sealed class ProcessingQueueServiceTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly string _outputFolder;
    private readonly DatabaseConnection _database;
    private readonly EpisodeRepository _episodes;
    private readonly ProcessJobRepository _jobs;
    private readonly ProcessedFileRepository _processedFiles;
    private readonly AppSettingsRepository _settings;
    private readonly FakeProcessingPipeline _pipeline;
    private readonly ProcessingQueueService _service;

    public ProcessingQueueServiceTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-st18-queue-" + Guid.NewGuid().ToString("N"));
        _outputFolder = Path.Combine(_workDirectory, "processed");
        Directory.CreateDirectory(_workDirectory);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _episodes = new EpisodeRepository(_database);
        _jobs = new ProcessJobRepository(_database);
        _processedFiles = new ProcessedFileRepository(_database);
        _settings = new AppSettingsRepository(_database);
        _settings.Set("processed_folder", _outputFolder);

        _pipeline = new FakeProcessingPipeline();
        _service = new ProcessingQueueService(_jobs, _episodes, _processedFiles, _pipeline, _settings);
    }

    public void Dispose()
    {
        _service.Dispose();
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

    private Episode SeedEpisode(int mediaItemId = 1, int? number = 1) =>
        _episodes.Insert(new Episode
        {
            MediaItemId = mediaItemId,
            EpisodeNumber = number,
            FileName = $"s{mediaItemId}-ep{number}.mkv",
            FilePath = Path.Combine(_workDirectory, $"s{mediaItemId}-ep{number}.mkv"),
        });

    /// <summary>Waits for <paramref name="count"/> terminal (completed/failed) job events.</summary>
    private Task WaitTerminalAsync(int count, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remaining = count;
        EventHandler<ProcessJob> handler = (_, _) =>
        {
            if (Interlocked.Decrement(ref remaining) <= 0)
            {
                tcs.TrySetResult();
            }
        };
        _service.JobCompleted += handler;
        _service.JobFailed += handler;
        return tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    /// <summary>Waits for the next <see cref="IProcessingQueueService.JobStarted"/> event.</summary>
    private Task<ProcessJob> WaitStartedAsync(int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource<ProcessJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.JobStarted += (_, job) => tcs.TrySetResult(job);
        return tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    // ── contract ───────────────────────────────────────────────────────────

    [Fact]
    public void Service_ImplementsIProcessingQueueService() =>
        typeof(ProcessingQueueService).Should().Implement<IProcessingQueueService>();

    // ── enqueue + persistence ──────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_PersistsQueuedJobs_InDatabase()
    {
        var e1 = SeedEpisode(number: 1);
        var e2 = SeedEpisode(number: 2);
        var e3 = SeedEpisode(number: 3);

        await _service.EnqueueAsync(new List<int> { e1.Id, e2.Id, e3.Id }, ProcessProfile.Local);

        var stored = _jobs.GetAll();
        stored.Should().HaveCount(3);
        stored.Should().OnlyContain(j => j.Status == JobStatus.Queued);
        stored.Select(j => j.EpisodeId).Should().BeEquivalentTo(new[] { e1.Id, e2.Id, e3.Id });
        _service.QueuedJobs.Should().HaveCount(3);
    }

    [Fact]
    public async Task Enqueue_DuplicateActiveJob_IsNoOp()
    {
        var ep = SeedEpisode();

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Should().HaveCount(1, "an active job is never duplicated");
        _service.QueuedJobs.Should().HaveCount(1);
    }

    // ── worker: success path ───────────────────────────────────────────────

    [Fact]
    public async Task Worker_ProcessesJob_SavesProcessedFile_AndCompletes()
    {
        var ep = SeedEpisode();
        await _service.StartAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await terminal;

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Completed);
        job.ProgressPct.Should().Be(100d);
        job.StartedAt.Should().NotBeNull();
        job.CompletedAt.Should().NotBeNull();

        ProcessedFile file = _processedFiles.GetByEpisodeAndProfile(ep.Id, ProcessProfile.Local)!;
        file.Should().NotBeNull();
        file.FilePath.Should().Be(Path.Combine(_outputFolder, $"{ep.Id}_local.mp4"));
        File.Exists(file.FilePath).Should().BeTrue();
        _pipeline.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Worker_DlnaProfile_BuildsDlnaConfigAndOutput()
    {
        var ep = SeedEpisode();
        await _service.StartAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Dlna);
        await terminal;

        _pipeline.Calls.Single().Config.Profile.Should().Be(ProcessProfile.Dlna);
        _pipeline.Calls.Single().Config.TargetWidth.Should().Be(3840);
        ProcessedFile file = _processedFiles.GetByEpisodeAndProfile(ep.Id, ProcessProfile.Dlna)!;
        file.FilePath.Should().EndWith($"{ep.Id}_dlna.mp4");
    }

    // ── worker: failure paths ──────────────────────────────────────────────

    [Fact]
    public async Task Worker_PipelineFailure_MarksFailed_WithErrorMessage()
    {
        var ep = SeedEpisode();
        _pipeline.Handler = (_, _, _, _) =>
            Task.FromResult(new ProcessResult(false, null, 0, TimeSpan.Zero, "encode boom"));
        await _service.StartAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await terminal;

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().Be("encode boom");
        _processedFiles.GetByEpisodeAndProfile(ep.Id, ProcessProfile.Local).Should().BeNull();
    }

    [Fact]
    public async Task Worker_PipelineThrows_MarksFailed()
    {
        var ep = SeedEpisode();
        _pipeline.Handler = (_, _, _, _) => throw new InvalidOperationException("pipeline exploded");
        await _service.StartAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await terminal;

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().Contain("pipeline exploded");
    }

    [Fact]
    public async Task Worker_UnknownEpisode_MarksFailed()
    {
        await _service.StartAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { 999 }, ProcessProfile.Local);
        await terminal;

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().Contain("999");
        _pipeline.CallCount.Should().Be(0, "a missing episode short-circuits before the pipeline");
    }

    // ── cancellation ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelCurrent_CancelsActiveJob()
    {
        var ep = SeedEpisode();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pipeline.Handler = async (_, _, _, ct) =>
        {
            // Block until cancelled (or released); cancellation surfaces as OCE.
            await using (ct.Register(() => release.TrySetResult()))
            {
                await release.Task.WaitAsync(ct);
            }

            return new ProcessResult(false, null, 0, TimeSpan.Zero, "Cancelled");
        };
        await _service.StartAsync();
        Task<ProcessJob> started = WaitStartedAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await started;

        await _service.CancelCurrentAsync();
        await terminal;

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Cancelled);
        _processedFiles.GetByEpisodeAndProfile(ep.Id, ProcessProfile.Local).Should().BeNull();
    }

    [Fact]
    public async Task ClearQueue_CancelsQueuedJobs_ButNotActive()
    {
        var e1 = SeedEpisode(number: 1);
        var e2 = SeedEpisode(number: 2);
        var e3 = SeedEpisode(number: 3);

        // The first job blocks until released; jobs 2 and 3 stay queued.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pipeline.Handler = async (_, _, _, _) =>
        {
            await release.Task;
            return new ProcessResult(true, Path.Combine(_outputFolder, "x.mp4"), 1, TimeSpan.Zero, null);
        };

        await _service.StartAsync();
        Task<ProcessJob> started = WaitStartedAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { e1.Id, e2.Id, e3.Id }, ProcessProfile.Local);
        await started; // job 1 is now active

        await _service.ClearQueueAsync();
        release.SetResult(); // let job 1 finish
        await terminal;

        ProcessJob job1 = _jobs.GetById(_jobs.GetAll().Single(j => j.EpisodeId == e1.Id).Id)!;
        job1.Status.Should().Be(JobStatus.Completed, "the active job is untouched by ClearQueue");

        var cleared = _jobs.GetAll().Where(j => j.EpisodeId == e2.Id || j.EpisodeId == e3.Id).ToList();
        cleared.Should().HaveCount(2);
        cleared.Should().OnlyContain(j => j.Status == JobStatus.Cancelled);

        _pipeline.CallCount.Should().Be(1, "queued jobs are drained before they reach the pipeline");
    }

    // ── crash recovery ─────────────────────────────────────────────────────

    [Fact]
    public void CrashRecovery_MarksProcessingJobs_Failed()
    {
        var ep = SeedEpisode();
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = ep.Id,
            Profile = ProcessProfile.Local,
            Status = JobStatus.Processing,
            StartedAt = DateTime.UtcNow,
        });

        _service.RecoverCrashedJobs();

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().NotBeNullOrEmpty();
        job.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StartAsync_RunsCrashRecovery_AndIsIdempotent()
    {
        var ep = SeedEpisode();
        _jobs.Insert(new ProcessJob { EpisodeId = ep.Id, Profile = ProcessProfile.Local, Status = JobStatus.Processing });

        await _service.StartAsync();
        await _service.StartAsync(); // idempotent — no throw

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Failed);
    }

    // ── progress propagation ───────────────────────────────────────────────

    [Fact]
    public async Task ProgressEvents_Propagate_FromPipeline()
    {
        var ep = SeedEpisode();
        var samples = new List<PipelineProgress>();
        _service.ProgressChanged += (_, p) => { lock (samples) { samples.Add(p); } };
        _pipeline.ProgressReports = 3;

        await _service.StartAsync();
        Task terminal = WaitTerminalAsync(1);

        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await terminal;

        lock (samples)
        {
            samples.Should().HaveCount(3);
            samples.Select(s => s.OverallPct).Should().BeInAscendingOrder();
        }
    }

    // ── reactivation of terminal jobs ──────────────────────────────────────

    [Fact]
    public async Task Enqueue_ReactivatesTerminalJob_InPlace()
    {
        var ep = SeedEpisode();
        await _service.StartAsync();

        Task first = WaitTerminalAsync(1);
        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await first;
        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Completed);

        Task second = WaitTerminalAsync(1);
        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await second;

        _jobs.GetAll().Should().HaveCount(1, "the unique (episode, profile) row is reused");
        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Completed);
        _pipeline.CallCount.Should().Be(2);
    }

    // ── lifecycle ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_StopsWorker_Gracefully()
    {
        await _service.StartAsync();
        await _service.StopAsync();

        // Enqueueing after stop persists but nothing processes (worker gone).
        var ep = SeedEpisode();
        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await Task.Delay(100);
        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Queued);
    }
}

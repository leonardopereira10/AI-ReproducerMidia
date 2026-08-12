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

    /// <summary>Creates a queue service sharing the test repositories but with a stubbed filesystem probe.</summary>
    private ProcessingQueueService CreateService(Func<string, bool> fileExists) =>
        new(_jobs, _episodes, _processedFiles, _pipeline, _settings, fileExists);

    /// <summary>
    /// Seeds an episode + <see cref="JobStatus.Completed"/> job + matching
    /// <see cref="ProcessedFile"/> so the skip logic can be exercised without running
    /// the worker. Filesystem state itself is stubbed via the injected fileExists probe.
    /// </summary>
    private (Episode Ep, ProcessJob Job, ProcessedFile Processed) SeedCompletedJob(
        string? episodeFileHash,
        string sourceHash,
        string filePath = @"C:\fake\output.mp4")
    {
        Episode ep = SeedEpisode();
        ep.FileHash = episodeFileHash;
        _episodes.Update(ep);

        ProcessJob job = _jobs.Insert(new ProcessJob
        {
            EpisodeId = ep.Id,
            Profile = ProcessProfile.Local,
            Status = JobStatus.Completed,
            ProgressPct = 100d,
            CompletedAt = DateTime.UtcNow,
        });

        ProcessedFile processed = _processedFiles.Insert(new ProcessedFile
        {
            EpisodeId = ep.Id,
            Profile = ProcessProfile.Local,
            FilePath = filePath,
            FileSizeBytes = 14,
            SourceHash = sourceHash,
            ProcessedAt = DateTime.UtcNow,
        });

        return (ep, job, processed);
    }

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

    // ── orphaned Queued recovery (subtask 02) ──────────────────────────────────

    /// <summary>
    /// Blocks the pipeline until released so queued jobs stay observable. When
    /// <paramref name="entered"/> is supplied it is signalled the moment the handler
    /// runs — deterministic proof the pipeline was invoked (JobStarted fires before
    /// the pipeline call, so awaiting JobStarted alone is not enough under load).
    /// </summary>
    private void BlockPipeline(TaskCompletionSource release, TaskCompletionSource? entered = null) =>
        _pipeline.Handler = async (_, _, _, ct) =>
        {
            entered?.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new ProcessResult(true, Path.Combine(_outputFolder, "x.mp4"), 1, TimeSpan.Zero, null);
        };

    [Fact]
    public async Task StartAsync_RecoversPersistedQueuedJobs_BackIntoMemoryQueue()
    {
        var e1 = SeedEpisode(number: 1);
        var e2 = SeedEpisode(number: 2);
        DateTime baseline = DateTime.UtcNow;

        // Inserted newer FIRST, older SECOND: recovery must order by CreatedAt, not row id.
        var newer = _jobs.Insert(new ProcessJob
        {
            EpisodeId = e1.Id, Profile = ProcessProfile.Local, Status = JobStatus.Queued,
            CreatedAt = baseline,
        });
        var older = _jobs.Insert(new ProcessJob
        {
            EpisodeId = e2.Id, Profile = ProcessProfile.Local, Status = JobStatus.Queued,
            CreatedAt = baseline.AddMinutes(-10),
        });

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockPipeline(release);

        Task<ProcessJob> started = WaitStartedAsync();
        await _service.StartAsync();
        ProcessJob first = await started;

        first.Id.Should().Be(older.Id, "recovery resumes the oldest Queued job first");

        List<ProcessJob> waiting = _service.QueuedJobs;
        waiting.Should().ContainSingle("the second persisted Queued job is back in the in-memory queue");
        waiting.Single().Id.Should().Be(newer.Id);
        waiting.Single().Status.Should().Be(JobStatus.Queued, "recovery does not touch the job status");

        _jobs.GetById(newer.Id)!.Status.Should().Be(JobStatus.Queued, "no status re-processing in the database");
        release.SetResult();
    }

    [Fact]
    public async Task StartAsync_RecoversQueuedJobs_AfterCrashRecovery()
    {
        var epCrashed = SeedEpisode(number: 1);
        var epQueued = SeedEpisode(number: 2);
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = epCrashed.Id, Profile = ProcessProfile.Local, Status = JobStatus.Processing,
            StartedAt = DateTime.UtcNow,
        });
        var queued = _jobs.Insert(new ProcessJob
        {
            EpisodeId = epQueued.Id, Profile = ProcessProfile.Local, Status = JobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        });

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredPipeline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockPipeline(release, enteredPipeline);

        Task<ProcessJob> started = WaitStartedAsync();
        await _service.StartAsync();
        ProcessJob resumed = await started;

        resumed.Id.Should().Be(queued.Id, "the persisted Queued job is resumed by the same StartAsync call");
        _jobs.GetAll().Single(j => j.EpisodeId == epCrashed.Id).Status
            .Should().Be(JobStatus.Failed, "crash recovery still marks orphaned Processing jobs Failed");
        _jobs.GetById(queued.Id)!.Status.Should().Be(JobStatus.Processing);
        await enteredPipeline.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _pipeline.CallCount.Should().Be(1, "only the Queued job reaches the pipeline");
        release.SetResult();
    }

    [Fact]
    public async Task StartAsync_NoQueuedJobs_QueueRemainsEmpty()
    {
        var ep = SeedEpisode();
        _jobs.Insert(new ProcessJob { EpisodeId = ep.Id, Profile = ProcessProfile.Local, Status = JobStatus.Processing });

        await _service.StartAsync();

        _service.QueuedJobs.Should().BeEmpty("there are no persisted Queued jobs to recover");
        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Failed, "crash recovery stays intact");
        _pipeline.CallCount.Should().Be(0, "nothing was written to the channel");
    }

    [Fact]
    public async Task StartAsync_SecondCall_DoesNotDuplicateRecoveredJobs()
    {
        var e1 = SeedEpisode(number: 1);
        var e2 = SeedEpisode(number: 2);
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = e1.Id, Profile = ProcessProfile.Local, Status = JobStatus.Queued, CreatedAt = DateTime.UtcNow,
        });
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = e2.Id, Profile = ProcessProfile.Local, Status = JobStatus.Queued, CreatedAt = DateTime.UtcNow,
        });

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredPipeline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockPipeline(release, enteredPipeline);

        Task<ProcessJob> started = WaitStartedAsync();
        await _service.StartAsync();
        await _service.StartAsync(); // idempotent — recovery must not run twice
        await started;
        await enteredPipeline.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _service.QueuedJobs.Should().HaveCount(1, "one job is active, one waits — no duplicates");
        _jobs.GetAll().Should().HaveCount(2);
        _pipeline.CallCount.Should().Be(1);
        release.SetResult();
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

        // The job now has a valid ProcessedFile (file written + matching hash), so a
        // plain re-enqueue would skip it — reactivation requires forceReprocess.
        Task second = WaitTerminalAsync(1);
        await _service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local, forceReprocess: true);
        await second;

        _jobs.GetAll().Should().HaveCount(1, "the unique (episode, profile) row is reused");
        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Completed);
        _pipeline.CallCount.Should().Be(2);
    }

    // ── completed-job skip (subtask 01) ─────────────────────────────────────

    [Fact]
    public async Task Enqueue_CompletedWithValidProcessedFile_Skips()
    {
        var (ep, _, _) = SeedCompletedJob(episodeFileHash: "hash-1", sourceHash: "hash-1");
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Completed, "a valid ProcessedFile skips the job");
        service.QueuedJobs.Should().BeEmpty("nothing may reach the channel");
        _pipeline.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Enqueue_CompletedWithValidProcessedFile_ForceReprocess_Reactivates()
    {
        var (ep, _, _) = SeedCompletedJob(episodeFileHash: "hash-1", sourceHash: "hash-1");
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local, forceReprocess: true);

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Queued, "forceReprocess bypasses the skip");
        job.ProgressPct.Should().Be(0d);
        service.QueuedJobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Enqueue_CompletedWithMissingFile_Reenqueues()
    {
        var (ep, _, _) = SeedCompletedJob(episodeFileHash: "hash-1", sourceHash: "hash-1");
        using ProcessingQueueService service = CreateService(_ => false);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Queued, "the output file vanished from disk");
        service.QueuedJobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Enqueue_CompletedWithStaleHash_Reenqueues()
    {
        var (ep, _, _) = SeedCompletedJob(episodeFileHash: "new-hash", sourceHash: "old-hash");
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Queued, "source changed since processing (stale)");
        service.QueuedJobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Enqueue_CompletedWithNullEpisodeHash_AndExistingFile_Skips()
    {
        var (ep, _, _) = SeedCompletedJob(episodeFileHash: null, sourceHash: string.Empty);
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Completed, "null source hash counts as not-stale");
        service.QueuedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Enqueue_CompletedWithEmptyFilePath_Reenqueues()
    {
        var (ep, _, _) = SeedCompletedJob(episodeFileHash: "hash-1", sourceHash: "hash-1", filePath: string.Empty);
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Queued, "an empty FilePath can never be valid");
        service.QueuedJobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Enqueue_CompletedWithoutProcessedFileRecord_Reenqueues()
    {
        var ep = SeedEpisode();
        ep.FileHash = "hash-1";
        _episodes.Update(ep);
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = ep.Id,
            Profile = ProcessProfile.Local,
            Status = JobStatus.Completed,
            CompletedAt = DateTime.UtcNow,
        });
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        _jobs.GetAll().Single().Status.Should().Be(JobStatus.Queued, "no ProcessedFile record means nothing to validate");
        service.QueuedJobs.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Enqueue_FailedOrCancelled_IsReactivated_RegardlessOfFileState(JobStatus status)
    {
        var ep = SeedEpisode();
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = ep.Id,
            Profile = ProcessProfile.Local,
            Status = status,
            ErrorMessage = "boom",
            CompletedAt = DateTime.UtcNow,
        });
        // Even a stub reporting an existing file must not skip non-Completed terminals.
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(JobStatus.Queued, $"a {status} job is always reactivated");
        job.ErrorMessage.Should().BeNull();
        service.QueuedJobs.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Processing)]
    public async Task Enqueue_ActiveJob_IsNoOp(JobStatus status)
    {
        var ep = SeedEpisode();
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = ep.Id,
            Profile = ProcessProfile.Local,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        });
        using ProcessingQueueService service = CreateService(_ => true);

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);

        ProcessJob job = _jobs.GetAll().Single();
        job.Status.Should().Be(status, "active jobs are never duplicated nor touched");
        service.QueuedJobs.Should().BeEmpty("no-op adds nothing to the channel");
    }

    [Fact]
    public async Task Enqueue_FakeRecords_ForceReprocessFlag()
    {
        var fake = new FakeProcessingQueue();

        await fake.EnqueueAsync(new List<int> { 1 }, ProcessProfile.Local);
        await fake.EnqueueAsync(new List<int> { 2 }, ProcessProfile.Dlna, forceReprocess: true);

        fake.EnqueueCalls[0].ForceReprocess.Should().BeFalse();
        fake.EnqueueCalls[1].ForceReprocess.Should().BeTrue();
        fake.EnqueueCalls[1].Profile.Should().Be(ProcessProfile.Dlna);
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

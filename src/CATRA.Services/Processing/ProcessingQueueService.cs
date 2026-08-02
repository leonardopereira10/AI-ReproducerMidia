using System.Threading.Channels;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Core.Processing;

namespace CATRA.Services.Processing;

/// <summary>
/// Default <see cref="IProcessingQueueService"/> (ST-18). A single dedicated worker
/// task drains an unbounded <see cref="Channel{T}"/> of <see cref="ProcessJob"/>s and
/// runs each through <see cref="IProcessingPipeline"/>, persisting the job lifecycle
/// (queued → processing → completed/failed/cancelled) and saving the resulting
/// <see cref="ProcessedFile"/> on success.
/// </summary>
/// <remarks>
/// <para>
/// All mutable state (queued list, current job, cancellation sources) is guarded by a
/// single lock; channel writes happen under that lock so <see cref="ClearQueueAsync"/>
/// can never race a partially-written enqueue. The worker re-reads each job from the
/// database just before processing so a job cancelled while queued is skipped.
/// </para>
/// <para>
/// The pipeline converts cancellation into a <see cref="ProcessResult"/> with
/// <c>ErrorMessage == "Cancelled"</c> rather than throwing; both that and a thrown
/// <see cref="OperationCanceledException"/> map to <see cref="JobStatus.Cancelled"/>.
/// </para>
/// </remarks>
public sealed class ProcessingQueueService : IProcessingQueueService, IDisposable
{
    private readonly IProcessJobRepository _jobs;
    private readonly IEpisodeRepository _episodes;
    private readonly IProcessedFileRepository _processedFiles;
    private readonly IProcessingPipeline _pipeline;
    private readonly IAppSettingsRepository _settings;

    private readonly Channel<ProcessJob> _channel =
        Channel.CreateUnbounded<ProcessJob>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _gate = new();
    private readonly List<ProcessJob> _queuedJobs = new();

    private CancellationTokenSource? _workerCts;
    private CancellationTokenSource? _currentJobCts;
    private Task? _workerTask;
    private ProcessJob? _currentJob;
    private bool _disposed;

    /// <summary>Creates the queue service over its dependencies.</summary>
    public ProcessingQueueService(
        IProcessJobRepository jobs,
        IEpisodeRepository episodes,
        IProcessedFileRepository processedFiles,
        IProcessingPipeline pipeline,
        IAppSettingsRepository settings)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public event EventHandler<ProcessJob>? JobStarted;

    /// <inheritdoc />
    public event EventHandler<ProcessJob>? JobCompleted;

    /// <inheritdoc />
    public event EventHandler<ProcessJob>? JobFailed;

    /// <inheritdoc />
    public event EventHandler<PipelineProgress>? ProgressChanged;

    /// <inheritdoc />
    public ProcessJob? CurrentJob
    {
        get { lock (_gate) { return _currentJob; } }
    }

    /// <inheritdoc />
    public List<ProcessJob> QueuedJobs
    {
        get { lock (_gate) { return _queuedJobs.ToList(); } }
    }

    /// <inheritdoc />
    public Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);

        lock (_gate)
        {
            foreach (int episodeId in episodeIds)
            {
                ProcessJob? existing = FindJob(episodeId, profile);
                ProcessJob job;

                if (existing is not null)
                {
                    if (existing.Status == JobStatus.Queued || existing.Status == JobStatus.Processing)
                    {
                        // Already active — never duplicate an in-flight job.
                        continue;
                    }

                    // Reactivate a terminal job in place: the ProcessJob table is unique
                    // per (EpisodeId, Profile), so we cannot insert a second row.
                    existing.Status = JobStatus.Queued;
                    existing.ProgressPct = 0d;
                    existing.CurrentStep = null;
                    existing.ErrorMessage = null;
                    existing.StartedAt = null;
                    existing.CompletedAt = null;
                    existing.CreatedAt = DateTime.UtcNow;
                    _jobs.Update(existing);
                    job = existing;
                }
                else
                {
                    job = new ProcessJob
                    {
                        EpisodeId = episodeId,
                        Profile = profile,
                        Status = JobStatus.Queued,
                        CreatedAt = DateTime.UtcNow,
                    };
                    _jobs.Insert(job);
                }

                _queuedJobs.Add(job);
                _channel.Writer.TryWrite(job);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelCurrentAsync()
    {
        lock (_gate)
        {
            _currentJobCts?.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearQueueAsync()
    {
        lock (_gate)
        {
            // Drain anything still in the channel.
            while (_channel.Reader.TryRead(out ProcessJob? job))
            {
                MarkCancelled(job);
            }

            // Belt-and-braces: cancel any tracked queued jobs the drain missed.
            foreach (ProcessJob job in _queuedJobs.ToList())
            {
                MarkCancelled(job);
            }

            _queuedJobs.Clear();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_workerTask is not null)
            {
                return Task.CompletedTask; // already running (idempotent)
            }

            RecoverCrashedJobs();

            _workerCts = new CancellationTokenSource();
            CancellationToken token = _workerCts.Token;
            _workerTask = Task.Run(() => LoopAsync(token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _workerCts;
            task = _workerTask;
            _workerCts = null;
            _workerTask = null;
            _currentJobCts?.Cancel();
        }

        if (cts is null || task is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The worker did not exit in time; leave it — the process is going down.
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop observes cancellation.
        }

        cts.Dispose();
    }

    /// <summary>
    /// Crash recovery (ST-18): any job left in <see cref="JobStatus.Processing"/> by an
    /// unclean shutdown is marked <see cref="JobStatus.Failed"/>. Called automatically
    /// by <see cref="StartAsync"/>; exposed for direct use/tests.
    /// </summary>
    public void RecoverCrashedJobs()
    {
        List<ProcessJob> stale = _jobs.GetAll()
            .Where(j => j.Status == JobStatus.Processing)
            .ToList();

        foreach (ProcessJob job in stale)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = "Interrupted by application restart (crash recovery).";
            job.CompletedAt = DateTime.UtcNow;
            _jobs.Update(job);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _workerCts?.Cancel();
            _currentJobCts?.Cancel();
            _currentJobCts?.Dispose();
            _workerCts?.Dispose();
        }
    }

    // --- worker loop -------------------------------------------------------

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (ProcessJob job in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await ProcessJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private async Task ProcessJobAsync(ProcessJob job)
    {
        lock (_gate)
        {
            _queuedJobs.Remove(job);
        }

        // Re-read: the job may have been cancelled while queued (ClearQueueAsync) or
        // removed. Skip anything that is no longer actionable.
        ProcessJob? fresh = _jobs.GetById(job.Id);
        if (fresh is null || fresh.Status != JobStatus.Queued)
        {
            return;
        }

        job = fresh;

        CancellationTokenSource jobCts = new();
        lock (_gate)
        {
            _currentJob = job;
            _currentJobCts = jobCts;
        }

        try
        {
            job.Status = JobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            _jobs.Update(job);
            JobStarted?.Invoke(this, job);

            Episode? episode = _episodes.GetById(job.EpisodeId);
            if (episode is null)
            {
                FailJob(job, $"Episode {job.EpisodeId} not found.");
                return;
            }

            PipelineConfig config = BuildConfig(job.Profile);
            IProgress<PipelineProgress> progress = new SyncProgress(sample =>
            {
                job.ProgressPct = sample.OverallPct;
                job.CurrentStep = MapStep(sample.CurrentStep);
                ProgressChanged?.Invoke(this, sample);
            });

            ProcessResult result;
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                       _workerCts?.Token ?? CancellationToken.None, jobCts.Token))
            {
                try
                {
                    result = await _pipeline
                        .ProcessAsync(episode, config, progress, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CancelJob(job);
                    return;
                }
            }

            if (result.Success)
            {
                SaveProcessedFile(job, episode, config, result);
                job.Status = JobStatus.Completed;
                job.ProgressPct = 100d;
                job.CompletedAt = DateTime.UtcNow;
                _jobs.Update(job);
                JobCompleted?.Invoke(this, job);
            }
            else if (IsCancellation(result, jobCts))
            {
                CancelJob(job);
            }
            else
            {
                FailJob(job, result.ErrorMessage ?? "Unknown processing error.");
            }
        }
        catch (OperationCanceledException)
        {
            CancelJob(job);
        }
        catch (Exception ex)
        {
            FailJob(job, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_currentJob, job))
                {
                    _currentJob = null;
                }

                if (ReferenceEquals(_currentJobCts, jobCts))
                {
                    _currentJobCts = null;
                }
            }

            jobCts.Dispose();
        }
    }

    private static bool IsCancellation(ProcessResult result, CancellationTokenSource jobCts) =>
        jobCts.IsCancellationRequested ||
        string.Equals(result.ErrorMessage, "Cancelled", StringComparison.Ordinal);

    private void CancelJob(ProcessJob job)
    {
        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        _jobs.Update(job);
        JobFailed?.Invoke(this, job);
    }

    private void FailJob(ProcessJob job, string errorMessage)
    {
        job.Status = JobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTime.UtcNow;
        _jobs.Update(job);
        JobFailed?.Invoke(this, job);
    }

    private void MarkCancelled(ProcessJob job)
    {
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            _jobs.Update(job);
        }
    }

    private void SaveProcessedFile(ProcessJob job, Episode episode, PipelineConfig config, ProcessResult result)
    {
        // Reprocessamento substitui o arquivo anterior (RN: delete + novo).
        ProcessedFile? existing = _processedFiles.GetByEpisodeAndProfile(episode.Id, job.Profile);
        if (existing is not null)
        {
            _processedFiles.Delete(existing.Id);
        }

        _processedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episode.Id,
            Profile = job.Profile,
            FilePath = result.OutputPath ?? string.Empty,
            FileSizeBytes = result.OutputSizeBytes,
            TargetFps = config.TargetFps,
            TargetWidth = config.TargetWidth,
            TargetHeight = config.TargetHeight,
            EncodeBitrate = config.EncodeBitrateKbps,
            InterpMethod = config.InterpMethod,
            UpscaleMethod = config.UpscaleMethod,
            ProcessedAt = DateTime.UtcNow,
            SourceHash = episode.FileHash ?? string.Empty,
        });
    }

    private ProcessJob? FindJob(int episodeId, ProcessProfile profile) =>
        _jobs.GetAll().FirstOrDefault(j => j.EpisodeId == episodeId && j.Profile == profile);

    // --- config ------------------------------------------------------------

    private PipelineConfig BuildConfig(ProcessProfile profile)
    {
        bool dlna = profile == ProcessProfile.Dlna;
        string prefix = dlna ? "dlna" : "local";

        int width = GetInt($"{prefix}_target_width", dlna ? 3840 : 1920);
        int height = GetInt($"{prefix}_target_height", dlna ? 2160 : 1080);
        double fps = GetDouble($"{prefix}_target_fps", dlna ? 55d : 135d);
        int bitrate = GetInt($"{prefix}_encode_bitrate_kbps", dlna ? 45_000 : 20_000);
        string interp = _settings.Get("interp_method") ?? "rife";
        string upscale = _settings.Get("upscale_method") ?? "fsr4";
        string? outputFolder = _settings.Get("processed_folder");
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            outputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CATRA",
                "processed");
        }

        return new PipelineConfig(profile, width, height, fps, bitrate, interp, upscale, outputFolder);
    }

    private int GetInt(string key, int fallback) =>
        int.TryParse(_settings.Get(key), out int value) ? value : fallback;

    private double GetDouble(string key, double fallback) =>
        double.TryParse(_settings.Get(key), out double value) ? value : fallback;

    private static ProcessStep MapStep(PipelineStep step) => step switch
    {
        PipelineStep.Decode => ProcessStep.Decode,
        PipelineStep.Interp => ProcessStep.Interp,
        PipelineStep.Upscale => ProcessStep.Upscale,
        PipelineStep.Encode => ProcessStep.Encode,
        // The job-table enum predates the Mux stage; report it as Encode (closest).
        PipelineStep.Mux => ProcessStep.Encode,
        _ => ProcessStep.Encode,
    };

    /// <summary>Synchronous <see cref="IProgress{T}"/> — invokes the callback inline.</summary>
    private sealed class SyncProgress : IProgress<PipelineProgress>
    {
        private readonly Action<PipelineProgress> _handler;

        public SyncProgress(Action<PipelineProgress> handler) => _handler = handler;

        public void Report(PipelineProgress value) => _handler(value);
    }
}

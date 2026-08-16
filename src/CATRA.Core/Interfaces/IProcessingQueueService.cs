using CATRA.Core.Enums;
using CATRA.Core.Models;
using CATRA.Core.Processing;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Thread-safe FIFO queue + dedicated background worker that pre-processes episodes
/// through <see cref="IProcessingPipeline"/> (ST-18, RF-03). Producers enqueue
/// episodes; a single consumer task drains the queue one job at a time, persists the
/// job lifecycle to the <c>ProcessJob</c> table, saves the resulting
/// <see cref="ProcessedFile"/> on success and surfaces progress/completion through
/// events so the UI can react without polling.
/// </summary>
/// <remarks>
/// Backed by <c>System.Threading.Channels</c> (unbounded, thread-safe). Every job is
/// persisted so its status survives a restart; on startup, jobs left in
/// <see cref="JobStatus.Processing"/> by a crash are recovered to
/// <see cref="JobStatus.Failed"/> and persisted <see cref="JobStatus.Queued"/> jobs
/// are re-enqueued for processing. Cancellation is cooperative via a per-job
/// <see cref="CancellationTokenSource"/>.
/// </remarks>
public interface IProcessingQueueService
{
    /// <summary>
    /// Enqueues episodes for processing under <paramref name="profile"/>. Each episode
    /// becomes a persisted <see cref="ProcessJob"/> in <see cref="JobStatus.Queued"/>.
    /// Re-enqueueing an episode that already has an active (queued/processing) job is a
    /// no-op; a failed/cancelled job is reactivated in place (the <c>ProcessJob</c>
    /// table is unique per episode+profile).
    /// </summary>
    /// <param name="episodeIds">Episodes to enqueue.</param>
    /// <param name="profile">Processing profile for the jobs.</param>
    /// <param name="forceReprocess">
    /// When <c>true</c>, bypasses the completed-job skip and reactivates even a valid
    /// <see cref="JobStatus.Completed"/> job. Defaults to <c>false</c>.
    /// </param>
    /// <remarks>
    /// A <see cref="JobStatus.Completed"/> job is skipped (stays completed, nothing
    /// queued) when its (episode, profile) <see cref="ProcessedFile"/> is valid: the
    /// output file exists on disk and its <c>SourceHash</c> matches the episode's
    /// current <c>FileHash</c> (a null/empty episode hash counts as valid — not stale).
    /// Missing output file, stale hash or missing processed-file record re-enqueue the
    /// job instead. Pass <paramref name="forceReprocess"/> = <c>true</c> to bypass.
    /// </remarks>
    Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile, bool forceReprocess = false);

    /// <summary>
    /// Cooperatively cancels the job currently being processed (if any). The pipeline
    /// observes the token and the job finishes as <see cref="JobStatus.Cancelled"/>.
    /// Queued jobs are untouched.
    /// </summary>
    Task CancelCurrentAsync();

    /// <summary>
    /// Removes every queued (not yet started) job, marking them
    /// <see cref="JobStatus.Cancelled"/> in the database. The active job is untouched
    /// (use <see cref="CancelCurrentAsync"/> for it).
    /// </summary>
    Task ClearQueueAsync();

    /// <summary>
    /// Starts the background worker (idempotent). Runs crash recovery first: jobs left
    /// in <see cref="JobStatus.Processing"/> by an unclean shutdown are marked
    /// <see cref="JobStatus.Failed"/>. Then every persisted
    /// <see cref="JobStatus.Queued"/> job is re-enqueued into the in-memory queue
    /// (oldest first), so jobs that survived a restart are resumed instead of staying
    /// stuck. Jobs already active in memory are never duplicated.
    /// </summary>
    Task StartAsync();

    /// <summary>Stops the background worker, waiting briefly for the loop to exit.</summary>
    Task StopAsync();

    /// <summary>The job currently being processed, or <c>null</c> when idle.</summary>
    ProcessJob? CurrentJob { get; }

    /// <summary>
    /// Snapshot of all jobs currently being processed (parallel processing).
    /// Empty when idle. With <c>MaxParallelJobs</c> = 1 this has at most one entry.
    /// </summary>
    List<ProcessJob> ActiveJobs { get; }

    /// <summary>Snapshot of the jobs waiting in the queue (not yet started).</summary>
    List<ProcessJob> QueuedJobs { get; }

    /// <summary>Raised when a job leaves the queue and starts processing.</summary>
    event EventHandler<ProcessJob>? JobStarted;

    /// <summary>Raised when a job completes successfully.</summary>
    event EventHandler<ProcessJob>? JobCompleted;

    /// <summary>Raised when a job fails or is cancelled.</summary>
    event EventHandler<ProcessJob>? JobFailed;

    /// <summary>Raised for every progress sample emitted by the pipeline.</summary>
    event EventHandler<PipelineProgress>? ProgressChanged;
}

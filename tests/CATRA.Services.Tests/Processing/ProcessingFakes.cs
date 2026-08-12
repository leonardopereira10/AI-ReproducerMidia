using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Core.Processing;

namespace CATRA.Services.Tests.Processing;

/// <summary>
/// In-memory <see cref="IProcessingPipeline"/> for ST-18 tests: records every call and,
/// by default, reports a couple of progress samples and "produces" a fake output file.
/// A custom <see cref="Handler"/> overrides the default behaviour (failure, cancellation,
/// blocking, ...). No GPU / native DLL / ffmpeg required.
/// </summary>
internal sealed class FakeProcessingPipeline : IProcessingPipeline
{
    private readonly object _gate = new();
    private readonly List<(Episode Episode, PipelineConfig Config)> _calls = new();

    /// <summary>Optional override for the whole processing behaviour.</summary>
    public Func<Episode, PipelineConfig, IProgress<PipelineProgress>, CancellationToken, Task<ProcessResult>>? Handler { get; set; }

    /// <summary>Whether the default behaviour writes a fake output file (default true).</summary>
    public bool WriteOutputFile { get; set; } = true;

    /// <summary>Number of progress samples the default behaviour reports (default 2).</summary>
    public int ProgressReports { get; set; } = 2;

    /// <summary>Every episode/config pair handed to <see cref="ProcessAsync"/>.</summary>
    public IReadOnlyList<(Episode Episode, PipelineConfig Config)> Calls
    {
        get { lock (_gate) { return _calls.ToList(); } }
    }

    public int CallCount
    {
        get { lock (_gate) { return _calls.Count; } }
    }

    public Task<ProcessResult> ProcessAsync(
        Episode episode,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((episode, config));
        }

        if (Handler is not null)
        {
            return Handler(episode, config, progress, cancellationToken);
        }

        return DefaultAsync(episode, config, progress, cancellationToken);
    }

    public Task<ProcessResult> ProcessBatchAsync(
        List<Episode> episodes,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The queue service processes one episode at a time.");

    private async Task<ProcessResult> DefaultAsync(
        Episode episode,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        int reports = Math.Max(1, ProgressReports);
        for (int i = 1; i <= reports; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double pct = i * 100d / reports;
            progress.Report(new PipelineProgress(0, 1, PipelineStep.Encode, pct, pct, TimeSpan.Zero, null));
        }

        string profile = config.Profile == ProcessProfile.Dlna ? "dlna" : "local";
        string outputPath = Path.Combine(config.OutputFolder, $"{episode.Id}_{profile}.mp4");

        if (WriteOutputFile)
        {
            Directory.CreateDirectory(config.OutputFolder);
            await File.WriteAllTextAsync(outputPath, "fake-processed", cancellationToken).ConfigureAwait(false);
        }

        return new ProcessResult(true, outputPath, 14, TimeSpan.Zero, null);
    }
}

/// <summary>Controllable <see cref="IProcessedFileUsage"/> for ST-18 tests.</summary>
internal sealed class FakeProcessedFileUsage : IProcessedFileUsage
{
    private readonly HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);

    public void SetInUse(string filePath, bool inUse = true)
    {
        if (inUse)
        {
            _inUse.Add(filePath);
        }
        else
        {
            _inUse.Remove(filePath);
        }
    }

    public bool IsInUse(string filePath) => _inUse.Contains(filePath);
}

/// <summary>
/// Recording <see cref="IProcessingQueueService"/> for <c>SlidingWindowService</c> tests:
/// captures enqueue/cancel/clear calls without running a real worker, so window logic is
/// asserted deterministically.
/// </summary>
internal sealed class FakeProcessingQueue : IProcessingQueueService
{
    private readonly object _gate = new();
    private readonly List<(List<int> Ids, ProcessProfile Profile, bool ForceReprocess)> _enqueueCalls = new();

    public IReadOnlyList<(List<int> Ids, ProcessProfile Profile, bool ForceReprocess)> EnqueueCalls
    {
        get { lock (_gate) { return _enqueueCalls.ToList(); } }
    }

    public int CancelCurrentCount { get; private set; }

    public int ClearQueueCount { get; private set; }

    /// <summary>Flattened episode ids enqueued, in order.</summary>
    public List<int> AllEnqueuedIds
    {
        get { lock (_gate) { return _enqueueCalls.SelectMany(c => c.Ids).ToList(); } }
    }

    public ProcessJob? CurrentJob { get; set; }

    public List<ProcessJob> QueuedJobs { get; } = new();

    public Task EnqueueAsync(List<int> episodeIds, ProcessProfile profile, bool forceReprocess = false)
    {
        lock (_gate)
        {
            _enqueueCalls.Add((episodeIds.ToList(), profile, forceReprocess));
        }

        return Task.CompletedTask;
    }

    public Task CancelCurrentAsync()
    {
        CancelCurrentCount++;
        return Task.CompletedTask;
    }

    public Task ClearQueueAsync()
    {
        ClearQueueCount++;
        return Task.CompletedTask;
    }

    public Task StartAsync() => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public event EventHandler<ProcessJob>? JobStarted
    {
        add { }
        remove { }
    }

    public event EventHandler<ProcessJob>? JobCompleted
    {
        add { }
        remove { }
    }

    public event EventHandler<ProcessJob>? JobFailed
    {
        add { }
        remove { }
    }

    public event EventHandler<PipelineProgress>? ProgressChanged
    {
        add { }
        remove { }
    }
}

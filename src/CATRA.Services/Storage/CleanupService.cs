using System.Diagnostics;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Storage;

/// <summary>
/// Default <see cref="ICleanupService"/> (ST-21, RF-04, RN-10). Deletes every processed
/// output file on shutdown (<see cref="CleanupAllAsync"/>) and removes crash orphans on
/// startup (<see cref="CleanupOrphansAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// A processed file is never force-deleted: files reported in use by
/// <see cref="IProcessedFileUsage"/> are skipped with a warning, and a
/// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> from
/// <see cref="File.Delete(string)"/> (file locked by the player/streamer) is retried
/// <see cref="DefaultMaxDeleteRetries"/> times with a <see cref="DefaultDeleteRetryDelay"/>
/// back-off before being reported in <see cref="CleanupResult.Errors"/>.
/// </para>
/// <para>
/// All mutating operations are serialized by a single <see cref="SemaphoreSlim"/> so a
/// startup cleanup can never overlap a shutdown cleanup. <see cref="CleanupAllAsync"/>
/// first cancels the active processing job (when a queue is available) and waits up to
/// <see cref="CancelWaitTimeout"/> for it to drain before touching any file.
/// </para>
/// </remarks>
public sealed class CleanupService : ICleanupService
{
    /// <summary>Default attempts to delete a locked processed file before giving up.</summary>
    public const int DefaultMaxDeleteRetries = 3;

    /// <summary>Default delay between delete retries for a locked file.</summary>
    public static readonly TimeSpan DefaultDeleteRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How long <see cref="CleanupAllAsync"/> waits for the active job to drain.</summary>
    public static readonly TimeSpan CancelWaitTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CancelPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProcessedFileRepository _processedFiles;
    private readonly IProcessJobRepository _jobs;
    private readonly IAppSettingsRepository _settings;
    private readonly IProcessingQueueService? _queue;
    private readonly IProcessedFileUsage _usage;
    private readonly int _maxDeleteRetries;
    private readonly TimeSpan _deleteRetryDelay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the cleanup service over its dependencies.</summary>
    /// <param name="processedFiles">Processed-file repository.</param>
    /// <param name="jobs">Process-job repository.</param>
    /// <param name="settings">App settings (processed folder location).</param>
    /// <param name="queue">Optional processing queue; cancelled before a full cleanup.</param>
    /// <param name="usage">Optional proactive in-use probe; defaults to "never in use".</param>
    /// <param name="maxDeleteRetries">Delete attempts for a locked file (default 3).</param>
    /// <param name="deleteRetryDelay">Delay between delete retries (default 500ms; shrink in tests).</param>
    public CleanupService(
        IProcessedFileRepository processedFiles,
        IProcessJobRepository jobs,
        IAppSettingsRepository settings,
        IProcessingQueueService? queue = null,
        IProcessedFileUsage? usage = null,
        int maxDeleteRetries = DefaultMaxDeleteRetries,
        TimeSpan? deleteRetryDelay = null)
    {
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _queue = queue;
        _usage = usage ?? new NeverInUse();
        _maxDeleteRetries = maxDeleteRetries < 1 ? DefaultMaxDeleteRetries : maxDeleteRetries;
        _deleteRetryDelay = deleteRetryDelay ?? DefaultDeleteRetryDelay;
    }

    /// <inheritdoc />
    public async Task<CleanupResult> CleanupAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 1. Cancel the active job and wait (bounded) for it to drain so no
            //    encoder is writing a file we are about to delete.
            if (_queue is not null)
            {
                await _queue.CancelCurrentAsync().ConfigureAwait(false);
                await WaitForQueueIdleAsync().ConfigureAwait(false);
            }

            var errors = new List<string>();
            int deleted = 0;
            long freed = 0;

            // 2. Delete every processed file on disk (retry locked files, skip in use).
            foreach (ProcessedFile processed in _processedFiles.GetAll())
            {
                long size = processed.FileSizeBytes ?? SafeFileSize(processed.FilePath);
                DeleteOutcome outcome = await TryDeleteFileAsync(processed.FilePath, errors).ConfigureAwait(false);
                if (outcome == DeleteOutcome.Deleted)
                {
                    deleted++;
                    freed += size;
                }
            }

            // 3. Clear the tables. Files that could not be deleted become disk orphans
            //    and are reclaimed by CleanupOrphansAsync on the next startup.
            _processedFiles.DeleteAll();
            _jobs.DeleteAll();

            Trace.WriteLine(
                $"Cleanup: {deleted} arquivos deletados, {freed / (1024d * 1024d * 1024d):F2} GB liberados");

            return new CleanupResult(deleted, freed, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CleanupResult> CleanupOrphansAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<string>();
            int deleted = 0;
            long freed = 0;

            string folder = GetProcessedFolder();
            if (Directory.Exists(folder))
            {
                IReadOnlyList<ProcessedFile> records = _processedFiles.GetAll();
                var knownPaths = new HashSet<string>(
                    records.Select(r => r.FilePath),
                    StringComparer.OrdinalIgnoreCase);

                // Files on disk without a database record → crash orphans → delete.
                foreach (string file in Directory.EnumerateFiles(folder, "*.mp4"))
                {
                    if (knownPaths.Contains(file))
                    {
                        continue;
                    }

                    if (_usage.IsInUse(file))
                    {
                        Trace.WriteLine($"Startup cleanup: arquivo em uso, ignorado: {file}");
                        errors.Add($"In use: {file}");
                        continue;
                    }

                    try
                    {
                        long size = SafeFileSize(file);
                        File.Delete(file);
                        deleted++;
                        freed += size;
                    }
                    catch (IOException ex)
                    {
                        Trace.WriteLine($"Startup cleanup: falha ao deletar órfão {file}: {ex.Message}");
                        errors.Add($"{file}: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Trace.WriteLine($"Startup cleanup: falha ao deletar órfão {file}: {ex.Message}");
                        errors.Add($"{file}: {ex.Message}");
                    }
                }

                // Records without a file on disk → stale → prune the record.
                foreach (ProcessedFile record in records)
                {
                    if (!File.Exists(record.FilePath))
                    {
                        _processedFiles.Delete(record.Id);
                    }
                }
            }

            Trace.WriteLine($"Startup cleanup: {deleted} órfãos deletados");
            return new CleanupResult(deleted, freed, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteProcessedAsync(int episodeId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<string>();
            foreach (ProcessedFile processed in _processedFiles.GetByEpisode(episodeId))
            {
                DeleteOutcome outcome = await TryDeleteFileAsync(processed.FilePath, errors).ConfigureAwait(false);
                if (outcome != DeleteOutcome.Failed)
                {
                    // Deleted (or already absent) → drop the record. On failure the
                    // record is kept so the file is not orphaned.
                    _processedFiles.Delete(processed.Id);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public long GetProcessedFolderSizeBytes()
    {
        string folder = GetProcessedFolder();
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            total += SafeFileSize(file);
        }

        return total;
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// Processed output folder: the configured <c>processed_folder</c> setting, or
    /// <c>%AppData%/CATRA/processed</c> by default (same resolution as the queue).
    /// </summary>
    private string GetProcessedFolder()
    {
        string? configured = _settings.Get(AppSettingsModel.ProcessedFolderKey);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CATRA",
            "processed");
    }

    /// <summary>Polls the queue until idle, bounded by <see cref="CancelWaitTimeout"/>.</summary>
    private async Task WaitForQueueIdleAsync()
    {
        if (_queue is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(CancelWaitTimeout);
        while (_queue.CurrentJob is not null && !timeout.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CancelPollInterval, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Deletes a file unless it is in use; retries on lock contention. Distinguishes a
    /// real deletion (<see cref="DeleteOutcome.Deleted"/>), an already-absent file
    /// (<see cref="DeleteOutcome.Absent"/>) and a locked/in-use file that survived
    /// every attempt (<see cref="DeleteOutcome.Failed"/>).
    /// </summary>
    private async Task<DeleteOutcome> TryDeleteFileAsync(string path, List<string> errors)
    {
        // Proactive guard: never even attempt an in-use (playing/streaming) file.
        if (_usage.IsInUse(path))
        {
            Trace.WriteLine($"Cleanup: arquivo em uso, ignorado: {path}");
            errors.Add($"In use: {path}");
            return DeleteOutcome.Failed;
        }

        for (int attempt = 0; attempt < _maxDeleteRetries; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return DeleteOutcome.Absent;
                }

                File.Delete(path);
                return DeleteOutcome.Deleted;
            }
            catch (IOException)
            {
                // Locked by the player/streamer — back off and retry, never force.
            }
            catch (UnauthorizedAccessException)
            {
                // Locked / read-only — back off and retry, never force.
            }

            if (attempt < _maxDeleteRetries - 1)
            {
                await Task.Delay(_deleteRetryDelay).ConfigureAwait(false);
            }
        }

        Trace.WriteLine($"Cleanup: não foi possível deletar (travado): {path}");
        errors.Add($"Locked: {path}");
        return DeleteOutcome.Failed;
    }

    private static long SafeFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private enum DeleteOutcome
    {
        /// <summary>The file existed and was removed from disk.</summary>
        Deleted,

        /// <summary>The file was already absent — nothing to delete, not an error.</summary>
        Absent,

        /// <summary>In use or locked — the file survived every attempt.</summary>
        Failed,
    }

    /// <summary>Default in-use probe: nothing is ever reported in use (reactive guard still applies).</summary>
    private sealed class NeverInUse : IProcessedFileUsage
    {
        public bool IsInUse(string filePath) => false;
    }
}

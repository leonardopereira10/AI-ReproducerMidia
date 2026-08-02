namespace CATRA.Core.Interfaces;

/// <summary>
/// Outcome of a cleanup operation (ST-21, RF-04, RN-10).
/// </summary>
/// <param name="FilesDeleted">Number of processed files removed from disk.</param>
/// <param name="BytesFreed">Total bytes freed by the deleted files.</param>
/// <param name="Errors">
/// Human-readable messages for files that could not be deleted (locked, in use,
/// I/O failure). Empty when every file was reclaimed.
/// </param>
public record CleanupResult(int FilesDeleted, long BytesFreed, List<string> Errors);

/// <summary>
/// Deletes processed output files on shutdown and removes crash orphans on startup
/// (ST-21, RF-04, RN-10). On close every <c>ProcessedFile</c> is reclaimed and the
/// <c>ProcessedFile</c>/<c>ProcessJob</c> tables are cleared; on startup files left on
/// disk by a crash (no database record) are deleted and stale records (no file on disk)
/// are pruned. A file reported in use by <see cref="IProcessedFileUsage"/> or locked by
/// another process is never force-deleted — it is skipped and reported in
/// <see cref="CleanupResult.Errors"/>.
/// </summary>
public interface ICleanupService
{
    /// <summary>
    /// Shutdown cleanup: cancels the active processing job, deletes every processed
    /// file on disk and clears the <c>ProcessedFile</c> and <c>ProcessJob</c> tables.
    /// Serialized by an internal semaphore — concurrent calls do not overlap.
    /// </summary>
    Task<CleanupResult> CleanupAllAsync();

    /// <summary>
    /// Startup crash recovery: deletes orphan <c>*.mp4</c> files in the processed
    /// folder that have no database record, and removes records whose file is gone.
    /// Always safe to run; never throws for a missing processed folder.
    /// </summary>
    Task<CleanupResult> CleanupOrphansAsync();

    /// <summary>
    /// Deletes the processed files (disk + database records) of a single episode.
    /// Files in use or locked are skipped (their records are kept).
    /// </summary>
    Task DeleteProcessedAsync(int episodeId);

    /// <summary>
    /// Total size in bytes of the files currently in the processed folder
    /// (zero when the folder does not exist).
    /// </summary>
    long GetProcessedFolderSizeBytes();
}

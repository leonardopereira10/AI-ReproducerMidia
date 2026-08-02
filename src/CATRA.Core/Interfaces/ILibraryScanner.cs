using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Scans the library root (<c>Root/Category/Series-or-Movie/files</c>) and syncs
/// it incrementally to the database (RF-01). Only new/changed/removed files are
/// processed; unchanged files are detected through their stored hash.
/// </summary>
public interface ILibraryScanner
{
    /// <summary>
    /// Runs an incremental scan.
    /// </summary>
    /// <param name="rootFolder">
    /// Library root; when <c>null</c> the <c>root_folder</c> app setting is used.
    /// </param>
    /// <param name="progress">Receives one report per level-2 folder processed.</param>
    /// <param name="cancellationToken">Cancels the scan between folders/files.</param>
    /// <exception cref="DirectoryNotFoundException">Root folder missing or not configured.</exception>
    Task<LibraryScanSummary> ScanAsync(
        string? rootFolder = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

namespace CATRA.Core.Models;

/// <summary>
/// Progress notification emitted while the library scanner walks level-2
/// (series/movie) folders (RF-01).
/// </summary>
public sealed class ScanProgress
{
    /// <summary>Total level-2 folders discovered under the root.</summary>
    public int TotalFolders { get; init; }

    /// <summary>Level-2 folders processed so far.</summary>
    public int ProcessedFolders { get; init; }

    /// <summary>Folder currently being processed.</summary>
    public string CurrentFolder { get; init; } = string.Empty;

    /// <summary>Completion percentage (0–100).</summary>
    public double Percent =>
        TotalFolders <= 0 ? 100d : ProcessedFolders * 100d / TotalFolders;
}

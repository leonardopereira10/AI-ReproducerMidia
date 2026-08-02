namespace CATRA.Core.Models;

/// <summary>
/// Aggregate counters of one incremental library scan (RF-01).
/// </summary>
public sealed class LibraryScanSummary
{
    /// <summary>Categories inserted.</summary>
    public int CategoriesAdded { get; set; }

    /// <summary>Categories pruned (folder removed from disk).</summary>
    public int CategoriesRemoved { get; set; }

    /// <summary>Media items inserted.</summary>
    public int MediaItemsAdded { get; set; }

    /// <summary>Media items updated (e.g. movie↔series re-detection, RN-01).</summary>
    public int MediaItemsUpdated { get; set; }

    /// <summary>Media items pruned (folder removed from disk).</summary>
    public int MediaItemsRemoved { get; set; }

    /// <summary>Episodes inserted.</summary>
    public int EpisodesAdded { get; set; }

    /// <summary>Episodes updated (file hash changed).</summary>
    public int EpisodesUpdated { get; set; }

    /// <summary>Episodes removed (file deleted from disk).</summary>
    public int EpisodesRemoved { get; set; }

    /// <summary>Files already cataloged with an unchanged hash (skipped).</summary>
    public int FilesUnchanged { get; set; }

    /// <summary>Scan wall-clock duration.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Whether any row was added, updated or removed.</summary>
    public bool HasChanges =>
        CategoriesAdded + CategoriesRemoved +
        MediaItemsAdded + MediaItemsUpdated + MediaItemsRemoved +
        EpisodesAdded + EpisodesUpdated + EpisodesRemoved > 0;
}

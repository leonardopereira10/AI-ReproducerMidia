namespace CATRA.Core.Models;

/// <summary>
/// Debounced batch of filesystem changes reported by
/// <see cref="Interfaces.ILibraryWatcher"/> (RF-01).
/// </summary>
public sealed class LibraryChangedEventArgs : EventArgs
{
    /// <summary>Creates an event carrying the changed paths.</summary>
    public LibraryChangedEventArgs(IReadOnlyList<string> changedPaths)
    {
        ChangedPaths = changedPaths ?? throw new ArgumentNullException(nameof(changedPaths));
    }

    /// <summary>Absolute paths created/deleted/renamed/modified in the burst.</summary>
    public IReadOnlyList<string> ChangedPaths { get; }
}

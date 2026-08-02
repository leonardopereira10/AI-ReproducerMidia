using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Watches the library root recursively for filesystem changes and raises a
/// single debounced event per burst of changes (RF-01). Consumers typically
/// trigger an incremental <see cref="ILibraryScanner"/> run from the event.
/// </summary>
public interface ILibraryWatcher : IDisposable
{
    /// <summary>
    /// Raised on a background thread after the debounce window elapses without
    /// further changes.
    /// </summary>
    event EventHandler<LibraryChangedEventArgs>? ChangesDetected;

    /// <summary>Whether the underlying watcher is currently active.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts watching <paramref name="rootFolder"/> recursively.
    /// Restarts cleanly when already running.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="rootFolder"/> does not exist.</exception>
    void Start(string rootFolder);

    /// <summary>Stops watching and discards pending changes. Safe when not running.</summary>
    void Stop();
}

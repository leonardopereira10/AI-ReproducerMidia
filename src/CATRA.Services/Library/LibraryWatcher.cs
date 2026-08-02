using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// <see cref="FileSystemWatcher"/> wrapper (RF-01): recursive watch of the library
/// root with a debounce window (default 2 s) that aggregates bursts of
/// created/deleted/renamed/changed events into a single
/// <see cref="ChangesDetected"/> notification.
/// <para>
/// File events are filtered to supported video extensions; directory events
/// (no extension) always pass so new series folders are noticed.
/// </para>
/// </summary>
public sealed class LibraryWatcher : ILibraryWatcher
{
    /// <summary>Default debounce window (spec RF-01: 2 seconds).</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv",
    };

    private readonly TimeSpan _debounce;
    private readonly object _sync = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Creates a watcher. <paramref name="debounce"/> defaults to 2 s; tests pass
    /// a short window.
    /// </summary>
    public LibraryWatcher(TimeSpan? debounce = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        if (_debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(debounce), "Debounce window must be positive.");
        }
    }

    /// <inheritdoc />
    public event EventHandler<LibraryChangedEventArgs>? ChangesDetected;

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _watcher is not null && _watcher.EnableRaisingEvents;
            }
        }
    }

    /// <inheritdoc />
    public void Start(string rootFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
        if (!Directory.Exists(rootFolder))
        {
            throw new DirectoryNotFoundException($"Library root not found: {rootFolder}");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();

            var watcher = new FileSystemWatcher(rootFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.Size
                             | NotifyFilters.LastWrite
                             | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Changed += OnFileSystemEvent;
            watcher.Renamed += OnRenamedEvent;
            watcher.Error += OnWatcherError;

            _timer = new Timer(OnDebounceElapsed);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }
    }

    private void StopCore()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileSystemEvent;
            _watcher.Deleted -= OnFileSystemEvent;
            _watcher.Changed -= OnFileSystemEvent;
            _watcher.Renamed -= OnRenamedEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        _timer?.Dispose();
        _timer = null;
        _pendingPaths.Clear();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Track(e.FullPath);

    private void OnRenamedEvent(object sender, RenamedEventArgs e) => Track(e.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or lost handle: stop watching. The next manual or
        // watcher-triggered scan reconciles the library; Start() can resume.
        Stop();
    }

    private void Track(string fullPath)
    {
        if (!IsRelevant(fullPath))
        {
            return;
        }

        lock (_sync)
        {
            if (_timer is null)
            {
                return; // stopped/disposed meanwhile
            }

            _pendingPaths.Add(fullPath);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static bool IsRelevant(string path)
    {
        var extension = Path.GetExtension(path);

        // Directories (no extension) matter — new/renamed series folders.
        // Files matter only when they are supported video formats.
        return extension.Length == 0 || SupportedExtensions.Contains(extension);
    }

    private void OnDebounceElapsed(object? state)
    {
        string[] paths;
        lock (_sync)
        {
            if (_pendingPaths.Count == 0)
            {
                return;
            }

            paths = _pendingPaths.ToArray();
            _pendingPaths.Clear();
        }

        ChangesDetected?.Invoke(this, new LibraryChangedEventArgs(paths));
    }
}

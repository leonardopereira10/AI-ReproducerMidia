using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Processing;

/// <summary>
/// Default <see cref="ISlidingWindowService"/> (ST-18, RF-03, RN-10). Keeps the next
/// <c>window_size</c> (default 5) unwatched episodes of a single series queued for
/// pre-processing and rotates the window as episodes are watched: the watched
/// episode's processed file is reclaimed and the next unwatched episode slides in.
/// </summary>
/// <remarks>
/// <para>
/// Only one series has an active window at a time. <see cref="StartWindowAsync"/> for
/// a different series stops the previous one (cancels the active job and clears the
/// queue). Window state is guarded by a single lock; repository/queue calls happen
/// outside the lock so it is never held across an <c>await</c>.
/// </para>
/// <para>
/// A processed file is never force-deleted: deletion is skipped when
/// <see cref="IProcessedFileUsage"/> reports the file in use, and a
/// <see cref="IOException"/> from <see cref="File.Delete(string)"/> (file locked by the
/// player/streamer) is retried <see cref="DefaultMaxDeleteRetries"/> times before giving
/// up — the database record is kept so the file is not orphaned.
/// </para>
/// </remarks>
public sealed class SlidingWindowService : ISlidingWindowService
{
    /// <summary>AppSettings key for the window size.</summary>
    public const string WindowSizeKey = "window_size";

    /// <summary>Spec default window size (RN-10).</summary>
    public const int DefaultWindowSize = 5;

    /// <summary>Default attempts to delete a locked processed file before giving up.</summary>
    public const int DefaultMaxDeleteRetries = 3;

    /// <summary>Default delay between delete retries for a locked file.</summary>
    public static readonly TimeSpan DefaultDeleteRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IEpisodeRepository _episodes;
    private readonly IProcessedFileRepository _processedFiles;
    private readonly IProcessingQueueService _queue;
    private readonly IAppSettingsRepository _settings;
    private readonly IProcessedFileUsage _usage;
    private readonly int _maxDeleteRetries;
    private readonly TimeSpan _deleteRetryDelay;

    private readonly object _gate = new();
    private List<Episode> _windowEpisodes = new();

    private int? _activeMediaItemId;
    private ProcessProfile? _activeProfile;

    /// <summary>Creates the sliding window service over its dependencies.</summary>
    /// <param name="episodes">Episode repository.</param>
    /// <param name="processedFiles">Processed-file repository.</param>
    /// <param name="queue">Processing queue (worker).</param>
    /// <param name="settings">App settings (window size).</param>
    /// <param name="usage">Optional proactive in-use probe; defaults to "never in use".</param>
    /// <param name="maxDeleteRetries">Delete attempts for a locked file (default 3).</param>
    /// <param name="deleteRetryDelay">Delay between delete retries (default 1s; shrink in tests).</param>
    public SlidingWindowService(
        IEpisodeRepository episodes,
        IProcessedFileRepository processedFiles,
        IProcessingQueueService queue,
        IAppSettingsRepository settings,
        IProcessedFileUsage? usage = null,
        int maxDeleteRetries = DefaultMaxDeleteRetries,
        TimeSpan? deleteRetryDelay = null)
    {
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usage = usage ?? new NeverInUse();
        _maxDeleteRetries = maxDeleteRetries < 1 ? DefaultMaxDeleteRetries : maxDeleteRetries;
        _deleteRetryDelay = deleteRetryDelay ?? DefaultDeleteRetryDelay;
    }

    /// <inheritdoc />
    public int? ActiveMediaItemId
    {
        get { lock (_gate) { return _activeMediaItemId; } }
    }

    /// <inheritdoc />
    public ProcessProfile? ActiveProfile
    {
        get { lock (_gate) { return _activeProfile; } }
    }

    /// <inheritdoc />
    public List<Episode> WindowEpisodes
    {
        get { lock (_gate) { return _windowEpisodes.ToList(); } }
    }

    /// <inheritdoc />
    public async Task StartWindowAsync(int mediaItemId, ProcessProfile profile)
    {
        int? previousId;
        lock (_gate)
        {
            previousId = _activeMediaItemId;
        }

        // Uma série por vez: parar a janela anterior (cancela job ativo + limpa fila).
        if (previousId.HasValue && previousId.Value != mediaItemId)
        {
            await StopWindowAsync(previousId.Value).ConfigureAwait(false);
        }

        int windowSize = GetWindowSize();
        List<Episode> window = _episodes
            .GetUnwatchedByMediaItem(mediaItemId)
            .Take(windowSize)
            .ToList();

        await _queue
            .EnqueueAsync(window.Select(e => e.Id).ToList(), profile)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _activeMediaItemId = mediaItemId;
            _activeProfile = profile;
            _windowEpisodes = window;
        }
    }

    /// <inheritdoc />
    public async Task StopWindowAsync(int mediaItemId)
    {
        bool isActive;
        lock (_gate)
        {
            isActive = _activeMediaItemId == mediaItemId;
        }

        // Stopping a series that has no active window is a no-op — it must not cancel
        // another series's jobs.
        if (!isActive)
        {
            return;
        }

        await _queue.CancelCurrentAsync().ConfigureAwait(false);
        await _queue.ClearQueueAsync().ConfigureAwait(false);

        lock (_gate)
        {
            if (_activeMediaItemId == mediaItemId)
            {
                _activeMediaItemId = null;
                _activeProfile = null;
                _windowEpisodes = new List<Episode>();
            }
        }
    }

    /// <inheritdoc />
    public async Task OnEpisodeWatchedAsync(int episodeId)
    {
        Episode? episode = _episodes.GetById(episodeId);
        if (episode is null)
        {
            return;
        }

        int? activeId;
        ProcessProfile? activeProfile;
        lock (_gate)
        {
            activeId = _activeMediaItemId;
            activeProfile = _activeProfile;
        }

        // Sem janela ativa ou episódio de outra série → nada a fazer.
        if (activeId is null || activeProfile is null || episode.MediaItemId != activeId.Value)
        {
            return;
        }

        // 1. Reclaim the watched episode's processed file (never force-delete in use).
        ProcessedFile? processed = _processedFiles.GetByEpisodeAndProfile(episodeId, activeProfile.Value);
        if (processed is not null && await TryDeleteProcessedFileAsync(processed).ConfigureAwait(false))
        {
            _processedFiles.Delete(processed.Id);
        }

        // 2. Slide the window: enqueue the next unwatched episode outside the window.
        List<int> currentWindowIds;
        lock (_gate)
        {
            currentWindowIds = _windowEpisodes.Select(e => e.Id).ToList();
        }

        Episode? next = _episodes
            .GetUnwatchedByMediaItem(activeId.Value)
            .FirstOrDefault(e => !currentWindowIds.Contains(e.Id));

        lock (_gate)
        {
            // Remove the watched episode from the window regardless of a successor.
            _windowEpisodes.RemoveAll(e => e.Id == episodeId);

            if (next is not null && !_windowEpisodes.Any(e => e.Id == next.Id))
            {
                _windowEpisodes.Add(next);
            }
        }

        if (next is not null)
        {
            await _queue
                .EnqueueAsync(new List<int> { next.Id }, activeProfile.Value)
                .ConfigureAwait(false);
        }
    }

    // --- helpers -----------------------------------------------------------

    private int GetWindowSize()
    {
        if (int.TryParse(_settings.Get(WindowSizeKey), out int size) && size > 0)
        {
            return size;
        }

        return DefaultWindowSize;
    }

    /// <summary>
    /// Deletes a processed file unless it is in use. Retries on
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> (file locked);
    /// returns <c>false</c> (and leaves the file untouched) when it cannot be deleted.
    /// </summary>
    private async Task<bool> TryDeleteProcessedFileAsync(ProcessedFile processed)
    {
        // Proactive guard: never even attempt an in-use (playing/streaming) file.
        if (_usage.IsInUse(processed.FilePath))
        {
            return false;
        }

        for (int attempt = 0; attempt < _maxDeleteRetries; attempt++)
        {
            try
            {
                if (File.Exists(processed.FilePath))
                {
                    File.Delete(processed.FilePath);
                }

                return true;
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

        return false;
    }

    /// <summary>Default in-use probe: nothing is ever reported in use (reactive guard still applies).</summary>
    private sealed class NeverInUse : IProcessedFileUsage
    {
        public bool IsInUse(string filePath) => false;
    }
}

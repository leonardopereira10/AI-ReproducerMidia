using System.Diagnostics;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// Incremental library scanner (RF-01). Walks <c>Root/Category/Series-or-Movie/files</c>,
/// detects movies vs series (RN-01), parses file names (RF-02 via
/// <see cref="IFilenameParser"/>), normalizes display names (RN-06 via
/// <see cref="NameNormalizer"/>), probes metadata through
/// <see cref="IMediaProbeService"/> and syncs everything to SQLite through the
/// repositories.
/// <para>
/// Incremental strategy: existing episodes are matched by normalized file path;
/// a file is re-processed only when its partial SHA-256 hash
/// (<see cref="FileHasher"/>) differs from <c>Episode.FileHash</c>. Rows whose
/// disk backing is gone are pruned (cascading watch state / processed files /
/// jobs so foreign keys stay intact).
/// </para>
/// <para>
/// Concurrent calls are serialized with a gate so a watcher-triggered scan and a
/// manual scan never interleave.
/// </para>
/// </summary>
public sealed class LibraryScanner : ILibraryScanner
{
    /// <summary>AppSettings key holding the library root folder.</summary>
    public const string RootFolderSettingKey = "root_folder";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv",
    };

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ICategoryRepository _categories;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IEpisodeRepository _episodes;
    private readonly IWatchStateRepository _watchStates;
    private readonly IProcessedFileRepository _processedFiles;
    private readonly IProcessJobRepository _processJobs;
    private readonly IAppSettingsRepository _settings;
    private readonly IFilenameParser _parser;
    private readonly IMediaProbeService _probe;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    /// <summary>Creates the scanner with all collaborators (DI-registered in App).</summary>
    public LibraryScanner(
        ICategoryRepository categories,
        IMediaItemRepository mediaItems,
        IEpisodeRepository episodes,
        IWatchStateRepository watchStates,
        IProcessedFileRepository processedFiles,
        IProcessJobRepository processJobs,
        IAppSettingsRepository settings,
        IFilenameParser parser,
        IMediaProbeService probe)
    {
        _categories = categories ?? throw new ArgumentNullException(nameof(categories));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _watchStates = watchStates ?? throw new ArgumentNullException(nameof(watchStates));
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _processJobs = processJobs ?? throw new ArgumentNullException(nameof(processJobs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc />
    public async Task<LibraryScanSummary> ScanAsync(
        string? rootFolder = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuredRoot = rootFolder ?? _settings.Get(RootFolderSettingKey);
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new DirectoryNotFoundException(
                $"Library root is not configured. Set the '{RootFolderSettingKey}' app setting " +
                "or pass an explicit root folder.");
        }

        if (!Directory.Exists(configuredRoot))
        {
            throw new DirectoryNotFoundException(
                $"Library root folder does not exist: '{configuredRoot}'.");
        }

        var root = NormalizePath(configuredRoot);

        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(root, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<LibraryScanSummary> ScanCoreAsync(
        string root,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var summary = new LibraryScanSummary();

        // Index current database state by normalized path for incremental comparison.
        var categoriesByPath = _categories.GetAll()
            .ToDictionary(c => NormalizePath(c.FolderPath), PathComparer);
        var mediaByPath = _mediaItems.GetAll()
            .ToDictionary(m => NormalizePath(m.FolderPath), PathComparer);
        var episodesByPath = _episodes.GetAll()
            .ToDictionary(e => NormalizePath(e.FilePath), PathComparer);

        // Discover level-2 folders up front so progress can report a stable total.
        var mediaFolders = Directory
            .EnumerateDirectories(root)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .SelectMany(categoryDir => Directory.EnumerateDirectories(categoryDir))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var processed = 0;
        foreach (var mediaFolder in mediaFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report(new ScanProgress
            {
                TotalFolders = mediaFolders.Count,
                ProcessedFolders = processed,
                CurrentFolder = mediaFolder,
            });

            var videoFiles = Directory
                .EnumerateFiles(mediaFolder)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (videoFiles.Count == 0)
            {
                continue; // level-2 folder without video files → not a media item
            }

            var categoryDir = Path.GetDirectoryName(mediaFolder)!;
            var category = EnsureCategory(categoryDir, categoriesByPath, summary);
            var mediaItem = EnsureMediaItem(mediaFolder, category, videoFiles, mediaByPath, summary);

            await SyncEpisodesAsync(mediaItem, videoFiles, episodesByPath, summary, cancellationToken)
                .ConfigureAwait(false);
        }

        PruneRemovedEntries(root, categoriesByPath, mediaByPath, episodesByPath, summary);

        stopwatch.Stop();
        summary.Elapsed = stopwatch.Elapsed;
        return summary;
    }

    private Category EnsureCategory(
        string categoryDir,
        Dictionary<string, Category> index,
        LibraryScanSummary summary)
    {
        var key = NormalizePath(categoryDir);
        if (index.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var name = DisplayTitle(Path.GetFileName(categoryDir));

        // Category.Name is UNIQUE: reuse a row by name (covers a moved/renamed root).
        var byName = _categories.GetByName(name);
        if (byName is not null)
        {
            if (!PathComparer.Equals(byName.FolderPath, categoryDir))
            {
                byName.FolderPath = categoryDir;
                _categories.Update(byName);
            }

            index[key] = byName;
            return byName;
        }

        var category = _categories.Insert(new Category
        {
            Name = name,
            FolderPath = categoryDir,
        });
        index[key] = category;
        summary.CategoriesAdded++;
        return category;
    }

    private MediaItem EnsureMediaItem(
        string mediaFolder,
        Category category,
        List<string> videoFiles,
        Dictionary<string, MediaItem> index,
        LibraryScanSummary summary)
    {
        var key = NormalizePath(mediaFolder);
        var detectedType = MediaTypeDetector.Detect(videoFiles.Count);

        if (index.TryGetValue(key, out var existing))
        {
            // RN-01 re-detection: file count may have crossed the movie↔series boundary.
            if (existing.MediaType != detectedType)
            {
                existing.MediaType = detectedType;
                _mediaItems.Update(existing);
                summary.MediaItemsUpdated++;
            }

            return existing;
        }

        var folderName = Path.GetFileName(mediaFolder);
        var item = _mediaItems.Insert(new MediaItem
        {
            CategoryId = category.Id,
            Title = ResolveItemTitle(folderName, videoFiles), // RN-06 display name
            RawFolderName = folderName,                       // RN-06 raw kept intact
            FolderPath = mediaFolder,
            MediaType = detectedType,
        });
        index[key] = item;
        summary.MediaItemsAdded++;
        return item;
    }

    /// <summary>
    /// Resolves the display title of a media item. RF-02: prefer the series
    /// name extracted from the files (P1/P2) — the folder itself may be a
    /// <c>[Site][Name]</c> tag soup that would normalize to an empty string.
    /// Falls back to the normalized folder name (RN-06), or the raw folder
    /// name when normalization yields nothing.
    /// </summary>
    private string ResolveItemTitle(string folderName, List<string> videoFiles)
    {
        foreach (var file in videoFiles)
        {
            var parse = _parser.Parse(Path.GetFileName(file));
            if (!string.IsNullOrWhiteSpace(parse.SeriesName))
            {
                return parse.SeriesName;
            }
        }

        return DisplayTitle(folderName);
    }

    private async Task SyncEpisodesAsync(
        MediaItem mediaItem,
        List<string> videoFiles,
        Dictionary<string, Episode> episodesByPath,
        LibraryScanSummary summary,
        CancellationToken cancellationToken)
    {
        var folderTitle = DisplayTitle(Path.GetFileName(mediaItem.FolderPath));
        var alivePaths = new HashSet<string>(PathComparer);

        foreach (var file in videoFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizePath(file);
            alivePaths.Add(key);

            string hash;
            try
            {
                hash = FileHasher.ComputeHash(file);
            }
            catch (IOException)
            {
                continue; // file busy/transient — picked up by the next scan
            }

            if (episodesByPath.TryGetValue(key, out var existing))
            {
                if (PathComparer.Equals(existing.FileHash, hash))
                {
                    summary.FilesUnchanged++;
                    continue; // unchanged → incremental skip
                }

                var probe = await _probe.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
                existing.FileHash = hash;
                existing.FileSizeBytes = SafeFileSize(file);
                ApplyProbe(existing, probe);
                _episodes.Update(existing);
                summary.EpisodesUpdated++;
                continue;
            }

            var probeResult = await _probe.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
            var parse = _parser.Parse(Path.GetFileName(file));

            var episode = new Episode
            {
                MediaItemId = mediaItem.Id,
                FileName = Path.GetFileName(file),
                FilePath = file,
                SeasonNumber = parse.SeasonNumber ?? 1,
                EpisodeNumber = parse.EpisodeNumber,
                DisplayTitle = BuildDisplayTitle(parse, folderTitle),
                FileHash = hash,
                FileSizeBytes = SafeFileSize(file),
            };
            ApplyProbe(episode, probeResult);

            // P4 fallback (RF-02): prefer the container title when it exists.
            if (parse.PatternUsed == FilenamePattern.Fallback &&
                !string.IsNullOrWhiteSpace(probeResult?.ContainerTitle))
            {
                episode.DisplayTitle = DisplayTitle(probeResult.ContainerTitle);
            }

            var inserted = _episodes.Insert(episode);
            episodesByPath[key] = inserted;
            summary.EpisodesAdded++;
        }

        // Episodes of this item whose file vanished → remove. Drop them from the
        // path index too so PruneRemovedEntries does not count them twice.
        foreach (var episode in _episodes.GetByMediaItem(mediaItem.Id))
        {
            var episodeKey = NormalizePath(episode.FilePath);
            if (!alivePaths.Contains(episodeKey))
            {
                DeleteEpisodeCascade(episode);
                episodesByPath.Remove(episodeKey);
                summary.EpisodesRemoved++;
            }
        }
    }

    /// <summary>
    /// Removes database rows whose disk backing disappeared under the scanned
    /// root: orphan episodes, then media item folders, then category folders.
    /// </summary>
    private void PruneRemovedEntries(
        string root,
        Dictionary<string, Category> categoriesByPath,
        Dictionary<string, MediaItem> mediaByPath,
        Dictionary<string, Episode> episodesByPath,
        LibraryScanSummary summary)
    {
        foreach (var episode in episodesByPath.Values.ToList())
        {
            if (IsUnderRoot(episode.FilePath, root) && !File.Exists(episode.FilePath))
            {
                DeleteEpisodeCascade(episode);
                summary.EpisodesRemoved++;
            }
        }

        foreach (var item in mediaByPath.Values.ToList())
        {
            if (!IsUnderRoot(item.FolderPath, root) || Directory.Exists(item.FolderPath))
            {
                continue;
            }

            foreach (var episode in _episodes.GetByMediaItem(item.Id))
            {
                DeleteEpisodeCascade(episode);
                summary.EpisodesRemoved++;
            }

            _mediaItems.Delete(item.Id);
            summary.MediaItemsRemoved++;
        }

        foreach (var category in categoriesByPath.Values.ToList())
        {
            if (!IsUnderRoot(category.FolderPath, root) || Directory.Exists(category.FolderPath))
            {
                continue;
            }

            foreach (var item in _mediaItems.GetByCategory(category.Id))
            {
                foreach (var episode in _episodes.GetByMediaItem(item.Id))
                {
                    DeleteEpisodeCascade(episode);
                    summary.EpisodesRemoved++;
                }

                _mediaItems.Delete(item.Id);
                summary.MediaItemsRemoved++;
            }

            _categories.Delete(category.Id);
            summary.CategoriesRemoved++;
        }
    }

    /// <summary>
    /// Deletes an episode together with dependent rows (processed files, jobs,
    /// watch state) so <c>PRAGMA foreign_keys=ON</c> is never violated.
    /// </summary>
    private void DeleteEpisodeCascade(Episode episode)
    {
        foreach (var processed in _processedFiles.GetByEpisode(episode.Id))
        {
            _processedFiles.Delete(processed.Id);
        }

        foreach (var job in _processJobs.GetAll().Where(j => j.EpisodeId == episode.Id).ToList())
        {
            _processJobs.Delete(job.Id);
        }

        var watchState = _watchStates.GetByEpisodeId(episode.Id);
        if (watchState is not null)
        {
            _watchStates.Delete(watchState.Id);
        }

        _episodes.Delete(episode.Id);
    }

    /// <summary>
    /// RN-06 display normalization that never yields an empty title: when the
    /// normalized form is blank (e.g. a folder named only <c>[Tags]</c>), the
    /// raw name is kept so the UI always has something to show.
    /// </summary>
    private static string DisplayTitle(string rawName)
    {
        var normalized = NameNormalizer.Normalize(rawName);
        return normalized.Length > 0 ? normalized : rawName;
    }

    private static string BuildDisplayTitle(FilenameParserResult parse, string folderTitle) =>
        parse.PatternUsed switch
        {
            FilenamePattern.Pattern1 or FilenamePattern.Pattern2 or FilenamePattern.PublisherRelease
                => $"Episódio {parse.EpisodeNumber ?? 0}",
            FilenamePattern.Pattern3
                => $"S{(parse.SeasonNumber ?? 1):D2}E{(parse.EpisodeNumber ?? 0):D2}",
            _ => folderTitle,
        };

    private static void ApplyProbe(Episode episode, MediaProbeResult? probe)
    {
        if (probe is null)
        {
            return;
        }

        episode.DurationSec = probe.DurationSec;
        episode.SourceFps = probe.Fps;
        episode.SourceWidth = probe.Width;
        episode.SourceHeight = probe.Height;
    }

    private static long? SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalized = NormalizePath(path);
        return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               (normalized.Length == root.Length ||
                normalized[root.Length] == Path.DirectorySeparatorChar);
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

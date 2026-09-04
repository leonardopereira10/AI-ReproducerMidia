using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Metadata;

/// <summary>
/// Default <see cref="IThumbnailService"/> (RF-08, RN-05). Frames are grabbed
/// through an injectable <see cref="IThumbnailExtractor"/> (ffmpeg CLI in
/// production, a stub in tests) and cached under
/// <c>%AppData%/CATRA/thumbs/</c>.
/// <para>
/// Extraction runs on the thread pool (<see cref="Task.Run(System.Action)"/>)
/// with a per-thumbnail timeout so a slow or hung ffmpeg never blocks callers.
/// Any failure degrades to <c>null</c>, which the UI renders as a placeholder.
/// </para>
/// </summary>
public sealed class ThumbnailService : IThumbnailService
{
    /// <summary>Videos shorter than this use the 30% timestamp (RN-05).</summary>
    public const double ShortVideoThresholdSec = 300d;

    /// <summary>Timestamp fraction for regular-length videos (RN-05: 10%).</summary>
    public const double RegularTimestampFraction = 0.10d;

    /// <summary>Timestamp fraction for short videos (RN-05: 30%).</summary>
    public const double ShortTimestampFraction = 0.30d;

    /// <summary>Fixed timestamp when the duration is unknown (RN-05: 30s).</summary>
    public const double UnknownDurationTimestampSec = 30d;

    /// <summary>Maximum time a single extraction may take.</summary>
    public static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(10);

    private readonly IEpisodeRepository _episodes;
    private readonly IMediaItemRepository _mediaItems;
    private readonly IThumbnailExtractor _extractor;
    private readonly string _cacheDirectory;

    /// <summary>
    /// Creates the service. <paramref name="cacheDirectory"/> defaults to
    /// <c>%AppData%/CATRA/thumbs</c>; tests pass a temporary folder.
    /// </summary>
    public ThumbnailService(
        IEpisodeRepository episodes,
        IMediaItemRepository mediaItems,
        IThumbnailExtractor extractor,
        string? cacheDirectory = null)
    {
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _mediaItems = mediaItems ?? throw new ArgumentNullException(nameof(mediaItems));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? DefaultCacheDirectory()
            : cacheDirectory;
    }

    /// <summary>
    /// Computes the RN-05 anti-copyright timestamp for a given duration:
    /// 10% of the duration, 30% when the video is under 5 minutes, and a fixed
    /// 30 seconds when the duration is unknown or non-positive.
    /// </summary>
    public static double ComputeTimestamp(double? durationSec)
    {
        if (durationSec is not { } duration || duration <= 0d)
        {
            return UnknownDurationTimestampSec;
        }

        var fraction = duration < ShortVideoThresholdSec
            ? ShortTimestampFraction
            : RegularTimestampFraction;

        return duration * fraction;
    }

    /// <inheritdoc />
    public async Task<string?> GetOrCreateThumbnailAsync(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (episode.Id <= 0)
        {
            return null;
        }

        var outputPath = EpisodeThumbnailPath(episode.Id);

        // Cache hit: never re-extract an existing thumbnail.
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var timestamp = ComputeTimestamp(episode.DurationSec);
        var extracted = await RunExtractionAsync(episode.FilePath, timestamp, outputPath)
            .ConfigureAwait(false);

        if (!extracted)
        {
            return null;
        }

        // Persist the resolved path so future scans can reuse it.
        episode.ThumbnailPath = outputPath;
        _episodes.Update(episode);
        return outputPath;
    }

    /// <inheritdoc />
    public async Task<string?> GetOrCreateSeriesCoverAsync(MediaItem mediaItem)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);
        if (mediaItem.Id <= 0)
        {
            return null;
        }

        // Priority 1: manual custom cover (persisted on MediaItem.CoverPath).
        if (!string.IsNullOrWhiteSpace(mediaItem.CoverPath) && File.Exists(mediaItem.CoverPath))
        {
            return mediaItem.CoverPath;
        }

        // Priority 2: previously extracted auto cover.
        var coverPath = SeriesCoverPath(mediaItem.Id);
        if (File.Exists(coverPath))
        {
            return coverPath;
        }

        // Priority 3: thumbnail já extraído de qualquer episódio (aleatório).
        var thumbCandidates = _episodes.GetByMediaItem(mediaItem.Id)
            .Where(e => !string.IsNullOrWhiteSpace(e.ThumbnailPath) && File.Exists(e.ThumbnailPath))
            .ToList();

        if (thumbCandidates.Count > 0)
        {
            return thumbCandidates[Random.Shared.Next(thumbCandidates.Count)].ThumbnailPath;
        }

        // Priority 4: extract from the first episode.
        var firstEpisode = FirstEpisode(mediaItem.Id);
        if (firstEpisode is null)
        {
            return null;
        }

        var timestamp = ComputeTimestamp(firstEpisode.DurationSec);
        var extracted = await RunExtractionAsync(firstEpisode.FilePath, timestamp, coverPath)
            .ConfigureAwait(false);

        return extracted ? coverPath : null;
    }

    /// <inheritdoc />
    public Task SetCustomCoverAsync(int mediaItemId, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Cover source image not found.", imagePath);
        }

        var item = _mediaItems.GetById(mediaItemId)
            ?? throw new ArgumentException($"Media item {mediaItemId} not found.", nameof(mediaItemId));

        Directory.CreateDirectory(_cacheDirectory);
        var customPath = CustomCoverPath(mediaItemId);
        File.Copy(imagePath, customPath, overwrite: true);

        item.CoverPath = customPath;
        _mediaItems.Update(item);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearCacheAsync()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return Task.CompletedTask;
        }

        foreach (var file in Directory.EnumerateFiles(_cacheDirectory))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort: a file in use survives until the next clear.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort: locked/ACL-protected file is skipped.
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Absolute cache path for an episode thumbnail.</summary>
    internal string EpisodeThumbnailPath(int episodeId) =>
        Path.Combine(_cacheDirectory, $"{episodeId}.jpg");

    /// <summary>Absolute cache path for an auto-generated series cover.</summary>
    internal string SeriesCoverPath(int mediaItemId) =>
        Path.Combine(_cacheDirectory, $"{mediaItemId}_cover.jpg");

    /// <summary>Absolute cache path for a manual custom series cover.</summary>
    internal string CustomCoverPath(int mediaItemId) =>
        Path.Combine(_cacheDirectory, $"{mediaItemId}_cover_custom.jpg");

    private Episode? FirstEpisode(int mediaItemId) =>
        _episodes.GetByMediaItem(mediaItemId)
            .OrderBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// Runs the extractor on the thread pool under a <see cref="ExtractionTimeout"/>
    /// guard. Returns <c>true</c> only when the output file exists afterwards.
    /// </summary>
    private async Task<bool> RunExtractionAsync(string inputPath, double timestamp, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return false;
        }

        Directory.CreateDirectory(_cacheDirectory);

        try
        {
            using var timeout = new CancellationTokenSource(ExtractionTimeout);
            var success = await Task.Run(
                    () => _extractor.ExtractFrameAsync(inputPath, timestamp, outputPath, timeout.Token),
                    timeout.Token)
                .ConfigureAwait(false);

            if (success && File.Exists(outputPath))
            {
                return true;
            }

            // Failed or produced nothing: drop any partial/corrupt file so the
            // next call re-extracts instead of cache-hitting a broken .jpg.
            DeletePartial(outputPath);
            return false;
        }
        catch (OperationCanceledException)
        {
            DeletePartial(outputPath);
            return false;
        }
        catch (Exception)
        {
            // Extraction is best-effort; any failure degrades to a placeholder.
            DeletePartial(outputPath);
            return false;
        }
    }

    /// <summary>Removes a partially-written output file, ignoring I/O races.</summary>
    private static void DeletePartial(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
        catch (IOException)
        {
            // Best effort: a file still held by ffmpeg survives this round.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: locked/ACL-protected file is skipped.
        }
    }

    private static string DefaultCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CATRA",
            "thumbs");
}

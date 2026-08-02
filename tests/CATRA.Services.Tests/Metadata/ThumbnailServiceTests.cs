using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Metadata;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Metadata;

/// <summary>
/// RF-08 / RN-05 tests for <see cref="ThumbnailService"/>. The ffmpeg binary
/// is NOT installed, so extraction runs through a stub
/// <see cref="IThumbnailExtractor"/>; repositories use a real temporary SQLite
/// database. These tests must pass without ffmpeg present.
/// </summary>
public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly string _cacheDirectory;
    private readonly DatabaseConnection _database;
    private readonly MediaItemRepository _mediaItems;
    private readonly EpisodeRepository _episodes;
    private readonly StubExtractor _extractor = new();
    private readonly ThumbnailService _service;

    public ThumbnailServiceTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-thumb-tests-" + Guid.NewGuid().ToString("N"));
        _cacheDirectory = Path.Combine(_workDirectory, "thumbs");
        Directory.CreateDirectory(_workDirectory);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _mediaItems = new MediaItemRepository(_database);
        _episodes = new EpisodeRepository(_database);

        _service = new ThumbnailService(_episodes, _mediaItems, _extractor, _cacheDirectory);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: WAL files may linger briefly on Windows.
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private MediaItem SeedMedia(string title = "Série A") =>
        _mediaItems.Insert(new MediaItem
        {
            CategoryId = 1,
            Title = title,
            RawFolderName = title,
            FolderPath = Path.Combine("root", title),
            MediaType = MediaType.Series,
        });

    private Episode SeedEpisode(int mediaItemId, string fileName, double? durationSec, int? number = 1) =>
        _episodes.Insert(new Episode
        {
            MediaItemId = mediaItemId,
            FileName = fileName,
            FilePath = Path.Combine("root", fileName),
            EpisodeNumber = number,
            DurationSec = durationSec,
        });

    private string EpisodeThumbPath(int episodeId) => Path.Combine(_cacheDirectory, $"{episodeId}.jpg");

    private string CoverPath(int mediaItemId) => Path.Combine(_cacheDirectory, $"{mediaItemId}_cover.jpg");

    private string CustomCoverPath(int mediaItemId) => Path.Combine(_cacheDirectory, $"{mediaItemId}_cover_custom.jpg");

    // ── RN-05 timestamp logic (pure) ───────────────────────────────────────

    [Theory]
    [InlineData(1000d, 100d)]   // regular video → 10%
    [InlineData(22d * 60, 132d)] // 22 min → 10%
    [InlineData(300d, 30d)]     // exactly 5 min is NOT "short" → 10%
    public void ComputeTimestamp_RegularDuration_UsesTenPercent(double duration, double expected) =>
        ThumbnailService.ComputeTimestamp(duration).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData(200d, 60d)]     // < 5 min → 30%
    [InlineData(100d, 30d)]
    [InlineData(299.9d, 89.97d)]
    public void ComputeTimestamp_ShortDuration_UsesThirtyPercent(double duration, double expected) =>
        ThumbnailService.ComputeTimestamp(duration).Should().BeApproximately(expected, 1e-6);

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-5d)]
    public void ComputeTimestamp_UnknownOrInvalidDuration_UsesFixed30Seconds(double? duration) =>
        ThumbnailService.ComputeTimestamp(duration).Should().Be(ThumbnailService.UnknownDurationTimestampSec);

    // ── cache: no re-extraction ────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateThumbnail_ExistingCacheFile_DoesNotReExtract()
    {
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllBytesAsync(EpisodeThumbPath(episode.Id), [1, 2, 3]);

        var result = await _service.GetOrCreateThumbnailAsync(episode);

        result.Should().Be(EpisodeThumbPath(episode.Id));
        _extractor.CallCount.Should().Be(0, "the cached file must short-circuit extraction");
    }

    [Fact]
    public async Task GetOrCreateThumbnail_NoCache_ExtractsAndPersistsPath()
    {
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);

        var result = await _service.GetOrCreateThumbnailAsync(episode);

        result.Should().Be(EpisodeThumbPath(episode.Id));
        _extractor.CallCount.Should().Be(1);
        _extractor.LastTimestamp.Should().BeApproximately(100d, 1e-9, "RN-05: 10% of 1000s");
        File.Exists(EpisodeThumbPath(episode.Id)).Should().BeTrue();

        // The resolved path is persisted so future scans can reuse it.
        _episodes.GetById(episode.Id)!.ThumbnailPath.Should().Be(EpisodeThumbPath(episode.Id));
    }

    [Fact]
    public async Task GetOrCreateThumbnail_ShortVideo_UsesThirtyPercentTimestamp()
    {
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "short.mp4", durationSec: 200d);

        await _service.GetOrCreateThumbnailAsync(episode);

        _extractor.LastTimestamp.Should().BeApproximately(60d, 1e-9, "RN-05: 30% for < 5min");
    }

    // ── failure → null (placeholder) ───────────────────────────────────────

    [Fact]
    public async Task GetOrCreateThumbnail_ExtractionFails_ReturnsNull()
    {
        _extractor.Succeed = false;
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);

        var result = await _service.GetOrCreateThumbnailAsync(episode);

        result.Should().BeNull("a failed extraction degrades to the UI placeholder");
        _episodes.GetById(episode.Id)!.ThumbnailPath.Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateThumbnail_ExtractorThrows_ReturnsNull()
    {
        _extractor.ThrowOnExtract = true;
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);

        var result = await _service.GetOrCreateThumbnailAsync(episode);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateThumbnail_FailureLeavingPartialFile_RemovesItAndReExtractsNextCall()
    {
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);

        // First attempt fails but leaves a corrupt/partial .jpg behind.
        _extractor.Succeed = false;
        _extractor.WritePartialOnFail = true;
        var first = await _service.GetOrCreateThumbnailAsync(episode);

        first.Should().BeNull();
        File.Exists(EpisodeThumbPath(episode.Id))
            .Should().BeFalse("a partial file must be removed so it cannot be cache-hit later");
        _extractor.CallCount.Should().Be(1);

        // Next attempt succeeds and must re-extract (no stale cache-hit).
        _extractor.Succeed = true;
        _extractor.WritePartialOnFail = false;
        var second = await _service.GetOrCreateThumbnailAsync(episode);

        second.Should().Be(EpisodeThumbPath(episode.Id));
        _extractor.CallCount.Should().Be(2, "the failed attempt must not poison the cache");
        File.Exists(EpisodeThumbPath(episode.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateThumbnail_MissingFilePath_ReturnsNullWithoutExtracting()
    {
        var media = SeedMedia();
        var episode = SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);
        episode.FilePath = "   ";

        var result = await _service.GetOrCreateThumbnailAsync(episode);

        result.Should().BeNull();
        _extractor.CallCount.Should().Be(0);
    }

    // ── series cover priority ──────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateSeriesCover_CustomCoverWins_WithoutExtraction()
    {
        var media = SeedMedia();
        SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);

        var source = Path.Combine(_workDirectory, "custom-src.jpg");
        await File.WriteAllBytesAsync(source, [9, 9, 9]);
        await _service.SetCustomCoverAsync(media.Id, source);

        var reloaded = _mediaItems.GetById(media.Id)!;
        var result = await _service.GetOrCreateSeriesCoverAsync(reloaded);

        result.Should().Be(CustomCoverPath(media.Id));
        _extractor.CallCount.Should().Be(0, "custom cover has top priority");
    }

    [Fact]
    public async Task GetOrCreateSeriesCover_ExistingAutoCover_DoesNotReExtract()
    {
        var media = SeedMedia();
        SeedEpisode(media.Id, "ep01.mp4", durationSec: 1000d);
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllBytesAsync(CoverPath(media.Id), [1, 2, 3]);

        var result = await _service.GetOrCreateSeriesCoverAsync(media);

        result.Should().Be(CoverPath(media.Id));
        _extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetOrCreateSeriesCover_NoCover_ExtractsFromFirstEpisode()
    {
        var media = SeedMedia();
        SeedEpisode(media.Id, "ep02.mp4", durationSec: 1000d, number: 2);
        var first = SeedEpisode(media.Id, "ep01.mp4", durationSec: 500d, number: 1);

        var result = await _service.GetOrCreateSeriesCoverAsync(media);

        result.Should().Be(CoverPath(media.Id));
        _extractor.CallCount.Should().Be(1);
        _extractor.LastInput.Should().Be(first.FilePath, "the lowest-numbered episode is used");
        _extractor.LastTimestamp.Should().BeApproximately(50d, 1e-9, "RN-05: 10% of 500s");
    }

    [Fact]
    public async Task GetOrCreateSeriesCover_NoEpisodes_ReturnsNull()
    {
        var media = SeedMedia();

        var result = await _service.GetOrCreateSeriesCoverAsync(media);

        result.Should().BeNull();
        _extractor.CallCount.Should().Be(0);
    }

    // ── custom cover persistence ───────────────────────────────────────────

    [Fact]
    public async Task SetCustomCover_CopiesFileAndUpdatesDatabase()
    {
        var media = SeedMedia();
        var source = Path.Combine(_workDirectory, "cover-src.png");
        await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);

        await _service.SetCustomCoverAsync(media.Id, source);

        File.Exists(CustomCoverPath(media.Id)).Should().BeTrue();
        _mediaItems.GetById(media.Id)!.CoverPath.Should().Be(CustomCoverPath(media.Id));
    }

    [Fact]
    public async Task SetCustomCover_MissingSource_Throws()
    {
        var media = SeedMedia();

        var act = () => _service.SetCustomCoverAsync(media.Id, Path.Combine(_workDirectory, "nope.jpg"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task SetCustomCover_UnknownMediaItem_Throws()
    {
        var source = Path.Combine(_workDirectory, "cover-src.png");
        await File.WriteAllBytesAsync(source, [1]);

        var act = () => _service.SetCustomCoverAsync(9999, source);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── clear cache ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearCache_DeletesAllCachedFiles()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDirectory, "1.jpg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDirectory, "2_cover.jpg"), [2]);

        await _service.ClearCacheAsync();

        Directory.EnumerateFiles(_cacheDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task ClearCache_NoDirectory_IsNoOp()
    {
        var act = () => _service.ClearCacheAsync();

        await act.Should().NotThrowAsync();
    }

    // ── stub extractor ─────────────────────────────────────────────────────

    /// <summary>
    /// Test double for <see cref="IThumbnailExtractor"/>: records each call and,
    /// when <see cref="Succeed"/> is true, materializes the output file so the
    /// service's existence check passes. Counts calls to prove caching.
    /// </summary>
    private sealed class StubExtractor : IThumbnailExtractor
    {
        public bool Succeed { get; set; } = true;

        public bool ThrowOnExtract { get; set; }

        /// <summary>When failing, leave a partial output file behind (simulates a killed ffmpeg).</summary>
        public bool WritePartialOnFail { get; set; }

        public int CallCount { get; private set; }

        public double LastTimestamp { get; private set; }

        public string? LastInput { get; private set; }

        public async Task<bool> ExtractFrameAsync(
            string inputPath,
            double timestampSec,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTimestamp = timestampSec;
            LastInput = inputPath;

            if (ThrowOnExtract)
            {
                throw new InvalidOperationException("boom");
            }

            if (!Succeed)
            {
                if (WritePartialOnFail)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await File.WriteAllBytesAsync(outputPath, [0xFF, 0xD8], cancellationToken);
                }

                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, [42], cancellationToken);
            return true;
        }
    }
}

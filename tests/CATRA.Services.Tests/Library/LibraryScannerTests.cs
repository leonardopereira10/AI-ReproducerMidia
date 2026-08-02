using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Library;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Library;

/// <summary>
/// RF-01 / RN-01 integration tests for <see cref="LibraryScanner"/>: real
/// repositories over a temporary SQLite database, a temporary library folder
/// tree on disk, and a stubbed <see cref="IMediaProbeService"/> (ffprobe is not
/// installed in the test environment — the scanner must never depend on it).
/// </summary>
public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly string _libraryRoot;
    private readonly DatabaseConnection _database;
    private readonly CategoryRepository _categories;
    private readonly MediaItemRepository _mediaItems;
    private readonly EpisodeRepository _episodes;
    private readonly WatchStateRepository _watchStates;
    private readonly ProcessedFileRepository _processedFiles;
    private readonly ProcessJobRepository _processJobs;
    private readonly AppSettingsRepository _settings;
    private readonly StubProbeService _probe;
    private readonly LibraryScanner _scanner;

    public LibraryScannerTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-scan-tests-" + Guid.NewGuid().ToString("N"));
        _libraryRoot = Path.Combine(_workDirectory, "library");
        Directory.CreateDirectory(_libraryRoot);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _categories = new CategoryRepository(_database);
        _mediaItems = new MediaItemRepository(_database);
        _episodes = new EpisodeRepository(_database);
        _watchStates = new WatchStateRepository(_database);
        _processedFiles = new ProcessedFileRepository(_database);
        _processJobs = new ProcessJobRepository(_database);
        _settings = new AppSettingsRepository(_database);
        _probe = new StubProbeService();

        _scanner = new LibraryScanner(
            _categories,
            _mediaItems,
            _episodes,
            _watchStates,
            _processedFiles,
            _processJobs,
            _settings,
            new FilenameParser(),
            _probe);
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

    private Task<LibraryScanSummary> ScanAsync(CancellationToken cancellationToken = default) =>
        _scanner.ScanAsync(_libraryRoot, cancellationToken: cancellationToken);

    /// <summary>Creates <c>root/category/series/fileName</c> with unique content.</summary>
    private string CreateVideoFile(string category, string series, string fileName, string? content = null)
    {
        var directory = Path.Combine(_libraryRoot, category, series);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content ?? $"fake-video-{Guid.NewGuid():N}");
        return path;
    }

    /// <summary>Creates any file (supported or not) under the library tree.</summary>
    private string CreateAnyFile(string category, string series, string fileName)
    {
        var directory = Path.Combine(_libraryRoot, category, series);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "not a video");
        return path;
    }

    // ── full scan: hierarchy + metadata ────────────────────────────────────

    [Fact]
    public async Task Scan_NewLibrary_PopulatesCategorySeriesEpisodeHierarchy()
    {
        // Arrange — spec P1 example, two episodes → series.
        const string seriesFolder = "[AniDong][A Record of a Mortal_s Journey]";
        CreateVideoFile("Animes", seriesFolder, "[AniDong][A Record of a Mortal_s Journey] - Episódio 26.mp4");
        CreateVideoFile("Animes", seriesFolder, "[AniDong][A Record of a Mortal_s Journey] - Episódio 27.mp4");
        var progress = new CollectingProgress();

        // Act
        var summary = await _scanner.ScanAsync(_libraryRoot, progress);

        // Assert — summary counters.
        summary.CategoriesAdded.Should().Be(1);
        summary.MediaItemsAdded.Should().Be(1);
        summary.EpisodesAdded.Should().Be(2);
        summary.HasChanges.Should().BeTrue();

        // Assert — level 1: category.
        var category = _categories.GetAll().Should().ContainSingle().Subject;
        category.Name.Should().Be("Animes");
        category.FolderPath.Should().Be(Path.Combine(_libraryRoot, "Animes"));

        // Assert — level 2: media item (RN-06 display title, raw kept intact).
        var item = _mediaItems.GetAll().Should().ContainSingle().Subject;
        item.CategoryId.Should().Be(category.Id);
        item.Title.Should().Be("A Record of a Mortal's Journey");
        item.RawFolderName.Should().Be(seriesFolder);
        item.MediaType.Should().Be(MediaType.Series);

        // Assert — level 3: episodes with parsed numbers + probed metadata.
        var episodes = _episodes.GetByMediaItem(item.Id);
        episodes.Should().HaveCount(2);
        episodes.Select(e => e.EpisodeNumber).Should().Equal(26, 27);
        episodes.Select(e => e.DisplayTitle).Should().Equal("Episódio 26", "Episódio 27");
        episodes.Should().OnlyContain(e => e.SeasonNumber == 1);
        episodes.Should().OnlyContain(e => e.MediaItemId == item.Id);
        episodes.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.FileHash));
        episodes.Should().OnlyContain(e => e.FileSizeBytes > 0);
        episodes.Should().OnlyContain(e => e.DurationSec == 1440.5);
        episodes.Should().OnlyContain(e => e.SourceFps == 23.976);
        episodes.Should().OnlyContain(e => e.SourceWidth == 1920 && e.SourceHeight == 1080);

        // Assert — progress reporting (one report per level-2 folder).
        progress.Reports.Should().ContainSingle();
        progress.Reports[0].TotalFolders.Should().Be(1);
        progress.Reports[0].ProcessedFolders.Should().Be(1);
        progress.Reports[0].Percent.Should().Be(100);
    }

    [Fact]
    public async Task Scan_Patterns_PersistEpisodeNumbersPerPattern()
    {
        // Arrange — P2, P3 and fallback files in distinct series folders.
        CreateVideoFile(
            "Animes",
            "Saikyou Series",
            "[AnimeFire.io] Saikyou Degarashi Ouji no Anyaku Teii Arasoi - Episódio 4 (HD).mp4");
        CreateVideoFile("Animes", "Acronym Series", "ACSADRGT01EP07.mp4", "content-p3-a");
        CreateVideoFile("Animes", "Acronym Series", "ACSADRGT01EP08.mp4", "content-p3-b");

        // Act
        await ScanAsync();

        // Assert — P2: episode number, display title.
        var p2 = _episodes.GetAll().Single(e => e.FileName.Contains("Saikyou"));
        p2.EpisodeNumber.Should().Be(4);
        p2.DisplayTitle.Should().Be("Episódio 4");

        // Assert — P3: season + episode, SxxExx display title.
        var p3 = _episodes.GetAll().Single(e => e.FileName == "ACSADRGT01EP07.mp4");
        p3.SeasonNumber.Should().Be(1);
        p3.EpisodeNumber.Should().Be(7);
        p3.DisplayTitle.Should().Be("S01E07");
    }

    [Fact]
    public async Task Scan_FallbackPattern_PrefersContainerTitleFromProbe()
    {
        // Arrange — P4 file; the probe stub carries a container title.
        var path = CreateVideoFile("Animes", "SUACLPLNDRS", "SUACLPLNDRS.mp4");
        _probe.SetResult(path, new MediaProbeResult(900, 24, 1280, 720, "suaclplndrs the movie"));

        // Act
        await ScanAsync();

        // Assert
        var episode = _episodes.GetAll().Should().ContainSingle().Subject;
        episode.EpisodeNumber.Should().BeNull();
        episode.SeasonNumber.Should().Be(1);
        episode.DisplayTitle.Should().Be("Suaclplndrs the Movie");
    }

    [Fact]
    public async Task Scan_FallbackPatternWithoutContainerTitle_UsesFolderName()
    {
        // Arrange — probe returns nothing usable (ffprobe missing scenario).
        var path = CreateVideoFile("Animes", "Plain Fallback", "random.mkv");
        _probe.SetResult(path, null);

        // Act
        await ScanAsync();

        // Assert
        var episode = _episodes.GetAll().Should().ContainSingle().Subject;
        episode.DisplayTitle.Should().Be("Plain Fallback");
    }

    // ── RN-01: movie vs series detection ───────────────────────────────────

    [Fact]
    public async Task Scan_SingleFileFolderIsMovie_TwoPlusFilesFolderIsSeries()
    {
        // Arrange
        CreateVideoFile("Filmes", "The Great Movie", "The.Great.Movie.mp4");
        CreateVideoFile("Animes", "Serie Brava", "Serie Brava 01.mp4", "sb-1");
        CreateVideoFile("Animes", "Serie Brava", "Serie Brava 02.mp4", "sb-2");

        // Act
        await ScanAsync();

        // Assert
        var movie = _mediaItems.GetAll().Single(m => m.RawFolderName == "The Great Movie");
        movie.MediaType.Should().Be(MediaType.Movie);

        var series = _mediaItems.GetAll().Single(m => m.RawFolderName == "Serie Brava");
        series.MediaType.Should().Be(MediaType.Series);
    }

    [Fact]
    public async Task Scan_MovieFolderGainsSecondFile_RedetectedAsSeries()
    {
        // Arrange — starts as a movie.
        CreateVideoFile("Filmes", "Solo Movie", "Solo.Movie.mp4", "solo-1");
        await ScanAsync();
        _mediaItems.GetAll().Single().MediaType.Should().Be(MediaType.Movie);

        // Act — a second file crosses the RN-01 boundary.
        CreateVideoFile("Filmes", "Solo Movie", "Solo.Movie.Part2.mp4", "solo-2");
        var summary = await ScanAsync();

        // Assert
        summary.MediaItemsUpdated.Should().Be(1);
        summary.EpisodesAdded.Should().Be(1);
        summary.FilesUnchanged.Should().Be(1);
        _mediaItems.GetAll().Single().MediaType.Should().Be(MediaType.Series);
        _episodes.GetAll().Should().HaveCount(2);
    }

    // ── incremental scan ───────────────────────────────────────────────────

    [Fact]
    public async Task Scan_SecondRun_DoesNotDuplicateRecords()
    {
        // Arrange
        CreateVideoFile("Animes", "Serie X", "Serie X 01.mp4", "x-1");
        CreateVideoFile("Animes", "Serie X", "Serie X 02.mp4", "x-2");
        var first = await ScanAsync();
        first.EpisodesAdded.Should().Be(2);
        var idsAfterFirst = _episodes.GetAll().Select(e => e.Id).OrderBy(id => id).ToArray();
        var probeCallsAfterFirst = _probe.CallCount;

        // Act
        var second = await ScanAsync();

        // Assert — nothing added, everything skipped as unchanged.
        second.CategoriesAdded.Should().Be(0);
        second.MediaItemsAdded.Should().Be(0);
        second.EpisodesAdded.Should().Be(0);
        second.EpisodesUpdated.Should().Be(0);
        second.EpisodesRemoved.Should().Be(0);
        second.FilesUnchanged.Should().Be(2);
        second.HasChanges.Should().BeFalse();

        _categories.GetAll().Should().HaveCount(1);
        _mediaItems.GetAll().Should().HaveCount(1);
        var idsAfterSecond = _episodes.GetAll().Select(e => e.Id).OrderBy(id => id).ToArray();
        idsAfterSecond.Should().Equal(idsAfterFirst);

        // Unchanged files must not be probed again.
        _probe.CallCount.Should().Be(probeCallsAfterFirst);
    }

    [Fact]
    public async Task Scan_RemovedFile_EpisodeIsRemovedOnNextScan()
    {
        // Arrange
        var removed = CreateVideoFile("Animes", "Serie Y", "Serie Y 01.mp4", "y-1");
        CreateVideoFile("Animes", "Serie Y", "Serie Y 02.mp4", "y-2");
        await ScanAsync();
        _episodes.GetAll().Should().HaveCount(2);

        // Act
        File.Delete(removed);
        var summary = await ScanAsync();

        // Assert — exactly one removal (no double counting), survivor intact.
        summary.EpisodesRemoved.Should().Be(1);
        summary.FilesUnchanged.Should().Be(1);
        summary.EpisodesAdded.Should().Be(0);
        var remaining = _episodes.GetAll().Should().ContainSingle().Subject;
        remaining.FileName.Should().Be("Serie Y 02.mp4");
        _mediaItems.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public async Task Scan_ModifiedFile_EpisodeIsUpdatedWithNewHash()
    {
        // Arrange
        var modified = CreateVideoFile("Animes", "Serie Z", "Serie Z 01.mp4", "original-content");
        CreateVideoFile("Animes", "Serie Z", "Serie Z 02.mp4", "z-2");
        await ScanAsync();
        var originalHash = _episodes.GetAll()
            .Single(e => e.FileName == "Serie Z 01.mp4").FileHash;

        // Act — same path, different bytes → different partial hash.
        File.WriteAllText(modified, "rewritten-content");
        var summary = await ScanAsync();

        // Assert
        summary.EpisodesUpdated.Should().Be(1);
        summary.FilesUnchanged.Should().Be(1);
        summary.EpisodesAdded.Should().Be(0);
        var updated = _episodes.GetAll().Single(e => e.FileName == "Serie Z 01.mp4");
        updated.FileHash.Should().NotBe(originalHash);
        _episodes.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public async Task Scan_RemovedSeriesFolder_MediaItemAndEpisodesArePruned()
    {
        // Arrange
        CreateVideoFile("Animes", "Serie A", "A 01.mp4", "a-1");
        CreateVideoFile("Animes", "Serie A", "A 02.mp4", "a-2");
        CreateVideoFile("Animes", "Serie B", "B 01.mp4", "b-1");
        CreateVideoFile("Filmes", "Movie C", "C.mp4", "c-1");
        await ScanAsync();
        _categories.GetAll().Should().HaveCount(2);
        _mediaItems.GetAll().Should().HaveCount(3);
        _episodes.GetAll().Should().HaveCount(4);

        // Act 1 — delete a series folder.
        Directory.Delete(Path.Combine(_libraryRoot, "Animes", "Serie A"), recursive: true);
        var summary1 = await ScanAsync();

        // Assert 1
        summary1.MediaItemsRemoved.Should().Be(1);
        summary1.EpisodesRemoved.Should().Be(2);
        summary1.CategoriesRemoved.Should().Be(0);
        _mediaItems.GetAll().Should().HaveCount(2);
        _episodes.GetAll().Should().HaveCount(2);

        // Act 2 — delete the whole category folder.
        Directory.Delete(Path.Combine(_libraryRoot, "Filmes"), recursive: true);
        var summary2 = await ScanAsync();

        // Assert 2 — media item, episode and now-empty category all pruned.
        summary2.CategoriesRemoved.Should().Be(1);
        summary2.MediaItemsRemoved.Should().Be(1);
        summary2.EpisodesRemoved.Should().Be(1);
        _categories.GetAll().Should().ContainSingle().Subject.Name.Should().Be("Animes");
        _mediaItems.GetAll().Should().ContainSingle().Subject.RawFolderName.Should().Be("Serie B");
        _episodes.GetAll().Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_DeletedFileWithDependentRows_CascadeDeletesKeepFkIntegrity()
    {
        // Arrange — movie with watch state, job and processed file rows.
        var video = CreateVideoFile("Filmes", "Cascade Movie", "Cascade.mp4");
        await ScanAsync();
        var episode = _episodes.GetAll().Should().ContainSingle().Subject;

        _watchStates.Insert(new WatchState { EpisodeId = episode.Id, ProgressPct = 42, LastPositionSec = 600 });
        _processJobs.Insert(new ProcessJob { EpisodeId = episode.Id, Profile = ProcessProfile.Local });
        _processedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episode.Id,
            Profile = ProcessProfile.Local,
            FilePath = Path.Combine(_workDirectory, "processed.mp4"),
            SourceHash = "abc",
        });

        // Act
        File.Delete(video);
        var summary = await ScanAsync();

        // Assert — episode and every dependent row are gone (FK-safe).
        summary.EpisodesRemoved.Should().Be(1);
        _episodes.GetAll().Should().BeEmpty();
        _watchStates.GetAll().Should().BeEmpty();
        _processJobs.GetAll().Should().BeEmpty();
        _processedFiles.GetAll().Should().BeEmpty();
    }

    // ── filtering + configuration ──────────────────────────────────────────

    [Fact]
    public async Task Scan_UnsupportedFiles_AreIgnored()
    {
        // Arrange — mixed folder (1 video + noise) and a docs-only folder.
        CreateVideoFile("Animes", "Mixed", "real episode.mkv");
        CreateAnyFile("Animes", "Mixed", "notes.txt");
        CreateAnyFile("Animes", "Mixed", "cover.jpg");
        CreateAnyFile("Animes", "DocsOnly", "readme.txt");

        // Act
        await ScanAsync();

        // Assert — docs-only folder produces neither media item nor category row.
        _episodes.GetAll().Should().ContainSingle().Subject.FileName.Should().Be("real episode.mkv");
        _mediaItems.GetAll().Should().ContainSingle().Subject.RawFolderName.Should().Be("Mixed");
        _categories.GetAll().Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_ExtensionsAreCaseInsensitive()
    {
        // Arrange
        CreateVideoFile("Animes", "Upper Case", "EP1.MP4", "u-1");
        CreateVideoFile("Animes", "Upper Case", "EP2.Mkv", "u-2");

        // Act
        await ScanAsync();

        // Assert
        _episodes.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public async Task Scan_NullRoot_UsesRootFolderAppSetting()
    {
        // Arrange
        CreateVideoFile("Animes", "From Settings", "From Settings 01.mp4");
        _settings.Set(LibraryScanner.RootFolderSettingKey, _libraryRoot);

        // Act
        var summary = await _scanner.ScanAsync();

        // Assert
        summary.CategoriesAdded.Should().Be(1);
        _episodes.GetAll().Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_NullRootWithoutSetting_Throws()
    {
        // Seeded default for root_folder is an empty string.
        var act = () => _scanner.ScanAsync();

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Scan_NonExistentRoot_Throws()
    {
        var missing = Path.Combine(_workDirectory, "does-not-exist");
        var act = () => _scanner.ScanAsync(missing);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Scan_CancelledToken_ThrowsOperationCanceled()
    {
        // Arrange
        CreateVideoFile("Animes", "Serie C", "C 01.mp4");
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act
        var act = () => ScanAsync(source.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── test doubles / helpers ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="IMediaProbeService"/> stub: ffprobe is not on PATH in the test
    /// environment, so the scanner is exercised with deterministic metadata.
    /// Records every probed path to verify incremental re-probe behavior.
    /// </summary>
    private sealed class StubProbeService : IMediaProbeService
    {
        private readonly object _sync = new();
        private readonly List<string> _probedPaths = new();
        private readonly Dictionary<string, MediaProbeResult?> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public MediaProbeResult? DefaultResult { get; set; } = new(1440.5, 23.976, 1920, 1080, null);

        public int CallCount
        {
            get { lock (_sync) { return _probedPaths.Count; } }
        }

        public void SetResult(string filePath, MediaProbeResult? result)
        {
            lock (_sync) { _overrides[filePath] = result; }
        }

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            lock (_sync) { _probedPaths.Add(filePath); }
            return Task.FromResult(_overrides.TryGetValue(filePath, out var result) ? result : DefaultResult);
        }
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> collector (no async races).</summary>
    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        private readonly object _sync = new();
        private readonly List<ScanProgress> _reports = new();

        public IReadOnlyList<ScanProgress> Reports
        {
            get { lock (_sync) { return _reports.ToList(); } }
        }

        public void Report(ScanProgress value)
        {
            lock (_sync) { _reports.Add(value); }
        }
    }
}

/// <summary>
/// RF-01 watcher tests: real <see cref="FileSystemWatcher"/> events with a
/// short debounce window so the suite stays fast yet exercises the real
/// debounce path (burst aggregation, extension filtering, stop/dispose).
/// </summary>
public sealed class LibraryWatcherTests : IDisposable
{
    private readonly string _root;

    public LibraryWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "catra-watch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void Watcher_NewVideoFile_RaisesChangesDetected()
    {
        // Arrange
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));
        var collector = new EventCollector();
        watcher.ChangesDetected += collector.OnChangesDetected;
        watcher.Start(_root);
        watcher.IsRunning.Should().BeTrue();

        // Act
        var created = Path.Combine(_root, "new episode.mp4");
        File.WriteAllText(created, "video");

        // Assert — event arrives with the created path.
        collector.WaitForFirstEvent().Should().BeTrue("the watcher must detect the new file");
        collector.AllPaths.Should().Contain(p => string.Equals(p, created, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Watcher_BurstOfChanges_RaisesSingleDebouncedEvent()
    {
        // Arrange — generous debounce so the whole burst lands in one window.
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(500));
        var collector = new EventCollector();
        watcher.ChangesDetected += collector.OnChangesDetected;
        watcher.Start(_root);

        // Act — three files in quick succession (one burst).
        var paths = new[]
        {
            Path.Combine(_root, "burst1.mp4"),
            Path.Combine(_root, "burst2.mkv"),
            Path.Combine(_root, "burst3.avi"),
        };
        foreach (var path in paths)
        {
            File.WriteAllText(path, "video");
        }

        // Assert — exactly one aggregated event containing every path.
        collector.WaitForFirstEvent().Should().BeTrue();
        collector.WaitUntilQuiet(TimeSpan.FromMilliseconds(2000));
        var events = collector.Events;
        events.Should().HaveCount(1, "the debounce window must aggregate the burst");
        events[0].ChangedPaths.Should().Contain(paths);
    }

    [Fact]
    public void Watcher_UnsupportedFile_DoesNotRaise()
    {
        // Arrange
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));
        var collector = new EventCollector();
        watcher.ChangesDetected += collector.OnChangesDetected;
        watcher.Start(_root);

        // Act
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a video");

        // Assert — no event after several debounce windows.
        collector.WaitForFirstEvent(TimeSpan.FromMilliseconds(1200)).Should().BeFalse();
        collector.Events.Should().BeEmpty();
    }

    [Fact]
    public void Watcher_Stop_EventsAreNoLongerRaised()
    {
        // Arrange
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));
        var collector = new EventCollector();
        watcher.ChangesDetected += collector.OnChangesDetected;
        watcher.Start(_root);
        watcher.Stop();

        // Act
        watcher.IsRunning.Should().BeFalse();
        File.WriteAllText(Path.Combine(_root, "after stop.mp4"), "video");

        // Assert
        collector.WaitForFirstEvent(TimeSpan.FromMilliseconds(1000)).Should().BeFalse();
    }

    [Fact]
    public void Watcher_Dispose_IsIdempotentAndStopsRaising()
    {
        // Arrange
        var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));
        var collector = new EventCollector();
        watcher.ChangesDetected += collector.OnChangesDetected;
        watcher.Start(_root);

        // Act
        watcher.Dispose();
        watcher.Dispose();
        File.WriteAllText(Path.Combine(_root, "after dispose.mp4"), "video");

        // Assert
        watcher.IsRunning.Should().BeFalse();
        collector.WaitForFirstEvent(TimeSpan.FromMilliseconds(1000)).Should().BeFalse();
    }

    [Fact]
    public void Watcher_Start_MissingFolder_Throws()
    {
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));
        var act = () => watcher.Start(Path.Combine(_root, "missing"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Watcher_Start_NullOrWhiteSpaceFolder_Throws(string? folder)
    {
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));
        var act = () => watcher.Start(folder!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Watcher_NonPositiveDebounce_Throws()
    {
        var act = () => new LibraryWatcher(TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Thread-safe collector for debounced watcher events.</summary>
    private sealed class EventCollector
    {
        private readonly object _sync = new();
        private readonly List<LibraryChangedEventArgs> _events = new();
        private readonly ManualResetEventSlim _firstEvent = new(false);

        public void OnChangesDetected(object? sender, LibraryChangedEventArgs e)
        {
            lock (_sync)
            {
                _events.Add(e);
            }

            _firstEvent.Set();
        }

        public IReadOnlyList<LibraryChangedEventArgs> Events
        {
            get { lock (_sync) { return _events.ToList(); } }
        }

        public IReadOnlyList<string> AllPaths
        {
            get
            {
                lock (_sync)
                {
                    return _events.SelectMany(e => e.ChangedPaths).ToList();
                }
            }
        }

        public bool WaitForFirstEvent(TimeSpan? timeout = null) =>
            _firstEvent.Wait(timeout ?? TimeSpan.FromSeconds(10));

        /// <summary>Sleeps a quiet period so any extra (unexpected) event can arrive.</summary>
        public void WaitUntilQuiet(TimeSpan quietPeriod) => Thread.Sleep(quietPeriod);
    }
}

using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Library;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Library;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Library;

/// <summary>
/// RF-01 / RF-07 / RN-03 tests for <see cref="LibraryService"/>: real
/// repositories over a temporary SQLite database plus a stubbed scanner.
/// </summary>
public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly DatabaseConnection _database;
    private readonly CategoryRepository _categories;
    private readonly MediaItemRepository _mediaItems;
    private readonly EpisodeRepository _episodes;
    private readonly WatchStateRepository _watchStates;
    private readonly StubScanner _scanner = new();
    private readonly LibraryService _service;

    public LibraryServiceTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-library-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _categories = new CategoryRepository(_database);
        _mediaItems = new MediaItemRepository(_database);
        _episodes = new EpisodeRepository(_database);
        _watchStates = new WatchStateRepository(_database);

        _service = new LibraryService(_categories, _mediaItems, _episodes, _watchStates, _scanner);
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

    private Category SeedCategory(string name) =>
        _categories.Insert(new Category { Name = name, FolderPath = Path.Combine("root", name) });

    private MediaItem SeedMedia(int categoryId, string title, MediaType type = MediaType.Series) =>
        _mediaItems.Insert(new MediaItem
        {
            CategoryId = categoryId,
            Title = title,
            RawFolderName = title,
            FolderPath = Path.Combine("root", title),
            MediaType = type,
        });

    private Episode SeedEpisode(int mediaItemId, int? number, string fileName) =>
        _episodes.Insert(new Episode
        {
            MediaItemId = mediaItemId,
            EpisodeNumber = number,
            FileName = fileName,
            FilePath = Path.Combine("root", fileName),
        });

    private void SeedWatchState(int episodeId, bool watched, double progressPct, double lastPositionSec) =>
        _watchStates.Insert(new WatchState
        {
            EpisodeId = episodeId,
            Watched = watched,
            ProgressPct = progressPct,
            LastPositionSec = lastPositionSec,
        });

    // ── categories ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCategoriesAsync_ReturnsCategoriesWithCounts_OrderedByName()
    {
        var animes = SeedCategory("Animes");
        var filmes = SeedCategory("Filmes");
        SeedCategory("Vazia");
        SeedMedia(animes.Id, "Série A");
        SeedMedia(animes.Id, "Série B");
        SeedMedia(filmes.Id, "Filme X", MediaType.Movie);

        var result = await _service.GetCategoriesAsync();

        result.Select(c => c.Name).Should().Equal("Animes", "Filmes", "Vazia");
        result.Select(c => c.MediaItemCount).Should().Equal(2, 1, 0);
    }

    // ── media items ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMediaItemsAsync_NullCategory_ReturnsEverything()
    {
        var animes = SeedCategory("Animes");
        var filmes = SeedCategory("Filmes");
        SeedMedia(animes.Id, "Série A");
        SeedMedia(filmes.Id, "Filme X", MediaType.Movie);

        var result = await _service.GetMediaItemsAsync(categoryId: null);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMediaItemsAsync_WithCategory_FiltersAndComputesProgress()
    {
        var animes = SeedCategory("Animes");
        var series = SeedMedia(animes.Id, "Série A");
        var other = SeedCategory("Filmes");
        SeedMedia(other.Id, "Filme X", MediaType.Movie);

        var ep1 = SeedEpisode(series.Id, 1, "ep01.mp4");
        var ep2 = SeedEpisode(series.Id, 2, "ep02.mp4");
        var ep3 = SeedEpisode(series.Id, 3, "ep03.mp4");
        SeedWatchState(ep1.Id, watched: true, progressPct: 100, lastPositionSec: 1300);
        SeedWatchState(ep2.Id, watched: false, progressPct: 40, lastPositionSec: 500);
        SeedWatchState(ep3.Id, watched: false, progressPct: 0, lastPositionSec: 0);

        var result = await _service.GetMediaItemsAsync(animes.Id);

        result.Should().HaveCount(1);
        var card = result[0];
        card.EpisodeCount.Should().Be(3);
        card.WatchedCount.Should().Be(1);
        card.IsFullyWatched.Should().BeFalse();
        card.HasContinueWatching.Should().BeTrue("ep2 has progress < 85% and position > 30s (RN-03)");
        card.ProgressDisplay.Should().Be("▶ 1/3");
        card.TypeDisplay.Should().Be("3 eps");
    }

    [Fact]
    public async Task GetMediaItemsAsync_FullyWatchedSeries_ShowsCheckBadge()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");
        var ep1 = SeedEpisode(series.Id, 1, "ep01.mp4");
        var ep2 = SeedEpisode(series.Id, 2, "ep02.mp4");
        SeedWatchState(ep1.Id, watched: true, progressPct: 100, lastPositionSec: 1300);
        SeedWatchState(ep2.Id, watched: true, progressPct: 100, lastPositionSec: 1300);

        var result = await _service.GetMediaItemsAsync(cat.Id);

        result[0].IsFullyWatched.Should().BeTrue();
        result[0].ProgressDisplay.Should().Be("✓");
    }

    [Fact]
    public async Task GetMediaItemsAsync_Movie_TypeDisplayIsFilme()
    {
        var cat = SeedCategory("Filmes");
        var movie = SeedMedia(cat.Id, "Filme X", MediaType.Movie);
        SeedEpisode(movie.Id, null, "filme.mp4");

        var result = await _service.GetMediaItemsAsync(cat.Id);

        result[0].TypeDisplay.Should().Be("Filme");
    }

    [Fact]
    public async Task GetMediaItemAsync_ReturnsItemOrNull()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");

        (await _service.GetMediaItemAsync(series.Id)).Should().NotBeNull();
        (await _service.GetMediaItemAsync(9999)).Should().BeNull();
    }

    // ── episodes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEpisodesAsync_ReturnsDetailsOrderedWithWatchState()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");
        var ep3 = SeedEpisode(series.Id, 3, "ep03.mp4");
        var ep1 = SeedEpisode(series.Id, 1, "ep01.mp4");
        var ep2 = SeedEpisode(series.Id, 2, "ep02.mp4");
        SeedWatchState(ep1.Id, watched: true, progressPct: 100, lastPositionSec: 1300);
        SeedWatchState(ep2.Id, watched: false, progressPct: 42, lastPositionSec: 540);

        var result = await _service.GetEpisodesAsync(series.Id);

        result.Select(d => d.Episode.EpisodeNumber).Should().Equal(1, 2, 3);
        result[0].IsWatched.Should().BeTrue();
        result[1].HasProgress.Should().BeTrue();
        result[1].ProgressPct.Should().Be(42);
        result[2].IsWatched.Should().BeFalse();
        result[2].HasProgress.Should().BeFalse();
        result[0].EpisodeLabel.Should().Be("EP01");
    }

    // ── toggle watched (RF-07) ─────────────────────────────────────────────

    [Fact]
    public async Task ToggleWatchedAsync_WithoutState_CreatesWatchedRow()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");
        var ep = SeedEpisode(series.Id, 1, "ep01.mp4");

        await _service.ToggleWatchedAsync(ep.Id);

        var state = _watchStates.GetByEpisodeId(ep.Id);
        state.Should().NotBeNull();
        state!.Watched.Should().BeTrue();
        state.ProgressPct.Should().Be(100);
    }

    [Fact]
    public async Task ToggleWatchedAsync_WatchedState_UnmarksAndKeepsProgress()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");
        var ep = SeedEpisode(series.Id, 1, "ep01.mp4");
        SeedWatchState(ep.Id, watched: true, progressPct: 100, lastPositionSec: 1300);

        await _service.ToggleWatchedAsync(ep.Id);

        var state = _watchStates.GetByEpisodeId(ep.Id);
        state!.Watched.Should().BeFalse();
        state.ProgressPct.Should().Be(100, "unmarking preserves stored progress");
    }

    [Fact]
    public async Task ToggleWatchedAsync_UnwatchedWithProgress_MarksWatchedAt100()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");
        var ep = SeedEpisode(series.Id, 1, "ep01.mp4");
        SeedWatchState(ep.Id, watched: false, progressPct: 42, lastPositionSec: 540);

        await _service.ToggleWatchedAsync(ep.Id);

        var state = _watchStates.GetByEpisodeId(ep.Id);
        state!.Watched.Should().BeTrue();
        state.ProgressPct.Should().Be(100);
    }

    [Fact]
    public async Task ToggleWatchedAsync_UnknownEpisode_Throws()
    {
        var act = () => _service.ToggleWatchedAsync(9999);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── continue watching (RN-03) ──────────────────────────────────────────

    [Fact]
    public async Task GetContinueWatchingAsync_AppliesRN03Thresholds()
    {
        var cat = SeedCategory("Animes");

        // Qualifies: progress < 85% and position > 30s, not watched.
        var qualifying = SeedMedia(cat.Id, "Continuando");
        var qEp = SeedEpisode(qualifying.Id, 1, "q.mp4");
        SeedWatchState(qEp.Id, watched: false, progressPct: 60, lastPositionSec: 400);

        // Excluded: already watched.
        var watched = SeedMedia(cat.Id, "Vista");
        var wEp = SeedEpisode(watched.Id, 1, "w.mp4");
        SeedWatchState(wEp.Id, watched: true, progressPct: 100, lastPositionSec: 1300);

        // Excluded: progress >= 85%.
        var advanced = SeedMedia(cat.Id, "Avancada");
        var aEp = SeedEpisode(advanced.Id, 1, "a.mp4");
        SeedWatchState(aEp.Id, watched: false, progressPct: 85, lastPositionSec: 1100);

        // Excluded: position <= 30s.
        var fresh = SeedMedia(cat.Id, "Nova");
        var fEp = SeedEpisode(fresh.Id, 1, "f.mp4");
        SeedWatchState(fEp.Id, watched: false, progressPct: 2, lastPositionSec: 20);

        // Excluded: never played.
        var never = SeedMedia(cat.Id, "Nunca");
        SeedEpisode(never.Id, 1, "n.mp4");

        var result = await _service.GetContinueWatchingAsync();

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Continuando");
        result[0].HasContinueWatching.Should().BeTrue();
    }

    // ── rename / cover ─────────────────────────────────────────────────────

    [Fact]
    public async Task RenameEpisodeAsync_PersistsDisplayTitle()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");
        var ep = SeedEpisode(series.Id, 1, "ep01.mp4");

        await _service.RenameEpisodeAsync(ep.Id, "  O Começo  ");

        _episodes.GetById(ep.Id)!.DisplayTitle.Should().Be("O Começo");
    }

    [Fact]
    public async Task RenameEpisodeAsync_UnknownEpisode_Throws()
    {
        var act = () => _service.RenameEpisodeAsync(9999, "x");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetMediaCoverAsync_PersistsAndClears()
    {
        var cat = SeedCategory("Animes");
        var series = SeedMedia(cat.Id, "Série A");

        await _service.SetMediaCoverAsync(series.Id, "C:/covers/a.png");
        _mediaItems.GetById(series.Id)!.CoverPath.Should().Be("C:/covers/a.png");

        await _service.SetMediaCoverAsync(series.Id, null);
        _mediaItems.GetById(series.Id)!.CoverPath.Should().BeNull();
    }

    [Fact]
    public async Task SetMediaCoverAsync_UnknownItem_Throws()
    {
        var act = () => _service.SetMediaCoverAsync(9999, "x.png");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── refresh / event ────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_ReturnsSummaryAndRaisesEvent()
    {
        _scanner.SummaryToReturn = new LibraryScanSummary { EpisodesAdded = 3 };
        LibraryScanSummary? received = null;
        _service.LibraryUpdated += (_, summary) => received = summary;

        var result = await _service.RefreshAsync();

        result.Should().BeSameAs(_scanner.SummaryToReturn);
        received.Should().BeSameAs(_scanner.SummaryToReturn);
        _scanner.ScanCount.Should().Be(1);
    }

    /// <summary>Scanner stub returning a canned summary.</summary>
    private sealed class StubScanner : ILibraryScanner
    {
        public LibraryScanSummary SummaryToReturn { get; set; } = new();

        public int ScanCount { get; private set; }

        public Task<LibraryScanSummary> ScanAsync(
            string? rootFolder = null,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            return Task.FromResult(SummaryToReturn);
        }
    }
}

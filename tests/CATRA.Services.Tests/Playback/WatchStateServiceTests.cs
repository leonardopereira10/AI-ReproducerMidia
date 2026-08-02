using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Playback;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Playback;

/// <summary>
/// RF-07 / RN-02 / RN-03 / RN-08 tests for <see cref="WatchStateService"/>: real
/// repositories over a temporary SQLite database (same harness as LibraryServiceTests).
/// </summary>
public sealed class WatchStateServiceTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly DatabaseConnection _database;
    private readonly EpisodeRepository _episodes;
    private readonly WatchStateRepository _watchStates;
    private readonly WatchStateService _service;

    public WatchStateServiceTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-watchstate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _episodes = new EpisodeRepository(_database);
        _watchStates = new WatchStateRepository(_database);

        _service = new WatchStateService(_watchStates, _episodes);
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

    private Episode SeedEpisode(int? number = 1, string? fileName = null, double? durationSec = null) =>
        _episodes.Insert(new Episode
        {
            MediaItemId = 1,
            EpisodeNumber = number,
            FileName = fileName ?? $"ep{number}.mkv",
            FilePath = Path.Combine("root", fileName ?? $"ep{number}.mkv"),
            DurationSec = durationSec,
        });

    private WatchState SeedState(int episodeId, double progressPct, double lastPositionSec, bool watched, DateTime updatedAt) =>
        _watchStates.Insert(new WatchState
        {
            EpisodeId = episodeId,
            ProgressPct = progressPct,
            LastPositionSec = lastPositionSec,
            Watched = watched,
            UpdatedAt = updatedAt,
        });

    // ── RN-02: threshold ───────────────────────────────────────────────────

    [Theory]
    [InlineData(120, 0.95)]    // 2min  -> 95%
    [InlineData(179, 0.95)]    // just under 3min -> 95%
    [InlineData(180, 0.90)]    // 3min boundary -> 90% band
    [InlineData(300, 0.90)]    // 5min  -> 90%
    [InlineData(1320, 0.8636)] // 22min -> ~86.4%
    [InlineData(7200, 0.975)]  // 2h    -> 97.5%
    public void CalculateThreshold_MatchesSpecMatrix(double durationSec, double expected)
        => WatchStateService.CalculateThreshold(durationSec)
            .Should().BeApproximately(expected, 0.0005);

    [Fact]
    public void CalculateThreshold_22Min_Is86Point4Pct()
        => (WatchStateService.CalculateThreshold(1320d) * 100d)
            .Should().BeApproximately(86.4d, 0.05d);

    // ── SaveProgress + auto-mark ───────────────────────────────────────────

    [Fact]
    public async Task SaveProgress_CreatesState_WithPositionAndProgress()
    {
        var ep = SeedEpisode(durationSec: 1320d);

        await _service.SaveProgressAsync(ep.Id, positionSec: 660d, durationSec: 1320d);

        var state = await _service.GetStateAsync(ep.Id);
        state.Should().NotBeNull();
        state!.LastPositionSec.Should().Be(660d);
        state.ProgressPct.Should().BeApproximately(50d, 0.001d);
        state.Watched.Should().BeFalse();
    }

    [Fact]
    public async Task SaveProgress_MarksWatched_WhenThresholdCrossed()
    {
        var ep = SeedEpisode(durationSec: 1320d); // threshold ~86.4% -> ~1140s

        await _service.SaveProgressAsync(ep.Id, positionSec: 1200d, durationSec: 1320d); // 90.9%

        var state = await _service.GetStateAsync(ep.Id);
        state!.Watched.Should().BeTrue();
    }

    [Fact]
    public async Task SaveProgress_DoesNotMarkWatched_BelowThreshold()
    {
        var ep = SeedEpisode(durationSec: 1320d);

        await _service.SaveProgressAsync(ep.Id, positionSec: 1000d, durationSec: 1320d); // 75.8%

        var state = await _service.GetStateAsync(ep.Id);
        state!.Watched.Should().BeFalse();
    }

    [Fact]
    public async Task SaveProgress_NeverRevertsManualWatched()
    {
        var ep = SeedEpisode(durationSec: 1320d);
        await _service.MarkWatchedAsync(ep.Id, watched: true);

        // A later low-progress save must not un-mark a manual watched flag.
        await _service.SaveProgressAsync(ep.Id, positionSec: 100d, durationSec: 1320d);

        var state = await _service.GetStateAsync(ep.Id);
        state!.Watched.Should().BeTrue();
        state.LastPositionSec.Should().Be(100d);
    }

    [Fact]
    public async Task SaveProgress_UnknownDuration_PersistsPositionOnly()
    {
        var ep = SeedEpisode();

        await _service.SaveProgressAsync(ep.Id, positionSec: 42d, durationSec: 0d);

        var state = await _service.GetStateAsync(ep.Id);
        state!.LastPositionSec.Should().Be(42d);
        state.ProgressPct.Should().Be(0d);
        state.Watched.Should().BeFalse();
    }

    // ── CheckAndMarkWatched ────────────────────────────────────────────────

    [Fact]
    public async Task CheckAndMarkWatched_MarksWhenCrossed()
    {
        var ep = SeedEpisode(durationSec: 300d); // threshold 90% -> 270s

        await _service.CheckAndMarkWatchedAsync(ep.Id, positionSec: 280d, durationSec: 300d);

        (await _service.GetStateAsync(ep.Id))!.Watched.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndMarkWatched_NoOpBelowThreshold()
    {
        var ep = SeedEpisode(durationSec: 300d);

        await _service.CheckAndMarkWatchedAsync(ep.Id, positionSec: 100d, durationSec: 300d);

        (await _service.GetStateAsync(ep.Id)).Should().BeNull();
    }

    // ── Manual toggle / mark ───────────────────────────────────────────────

    [Fact]
    public async Task ToggleWatched_CreatesWatchedRow_OnFirstUse()
    {
        var ep = SeedEpisode();

        await _service.ToggleWatchedAsync(ep.Id);

        var state = await _service.GetStateAsync(ep.Id);
        state!.Watched.Should().BeTrue();
        state.ProgressPct.Should().Be(100d);
    }

    [Fact]
    public async Task ToggleWatched_FlipsExistingFlag()
    {
        var ep = SeedEpisode();
        await _service.ToggleWatchedAsync(ep.Id); // -> watched

        await _service.ToggleWatchedAsync(ep.Id); // -> unwatched

        (await _service.GetStateAsync(ep.Id))!.Watched.Should().BeFalse();
    }

    [Fact]
    public async Task MarkWatched_SetsExplicitFlag()
    {
        var ep = SeedEpisode();

        await _service.MarkWatchedAsync(ep.Id, watched: true);
        (await _service.GetStateAsync(ep.Id))!.Watched.Should().BeTrue();

        await _service.MarkWatchedAsync(ep.Id, watched: false);
        (await _service.GetStateAsync(ep.Id))!.Watched.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleWatched_UnknownEpisode_Throws()
    {
        Func<Task> act = () => _service.ToggleWatchedAsync(999);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── RN-03: continue watching ───────────────────────────────────────────

    [Fact]
    public async Task GetContinueWatching_FiltersAndOrdersByUpdatedAtDesc()
    {
        var epA = SeedEpisode(number: 1, fileName: "a.mkv");
        var epB = SeedEpisode(number: 2, fileName: "b.mkv");
        var epC = SeedEpisode(number: 3, fileName: "c.mkv");
        var epD = SeedEpisode(number: 4, fileName: "d.mkv");
        var epE = SeedEpisode(number: 5, fileName: "e.mkv");

        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedState(epA.Id, progressPct: 50d, lastPositionSec: 120d, watched: false, updatedAt: t0.AddDays(1)); // eligible (older)
        SeedState(epB.Id, progressPct: 40d, lastPositionSec: 200d, watched: false, updatedAt: t0.AddDays(3)); // eligible (newest)
        SeedState(epC.Id, progressPct: 90d, lastPositionSec: 500d, watched: false, updatedAt: t0.AddDays(5)); // progress >= 85 -> out
        SeedState(epD.Id, progressPct: 50d, lastPositionSec: 10d, watched: false, updatedAt: t0.AddDays(4));  // pos <= 30 -> out
        SeedState(epE.Id, progressPct: 50d, lastPositionSec: 120d, watched: true, updatedAt: t0.AddDays(6));  // watched -> out

        var result = await _service.GetContinueWatchingAsync();

        result.Select(e => e.Id).Should().ContainInOrder(epB.Id, epA.Id);
        result.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(50d, 120d, false, true)]   // eligible
    [InlineData(85d, 120d, false, false)]  // progress at 85% -> no
    [InlineData(50d, 30d, false, false)]   // position at 30s -> no
    [InlineData(0d, 120d, false, false)]   // no progress -> no
    [InlineData(50d, 120d, true, false)]   // watched -> no
    public async Task ShouldOfferContinue_Matrix(double pct, double pos, bool watched, bool expected)
    {
        var ep = SeedEpisode();
        SeedState(ep.Id, pct, pos, watched, DateTime.UtcNow);

        (await _service.ShouldOfferContinueAsync(ep.Id)).Should().Be(expected);
    }

    [Fact]
    public async Task ShouldOfferContinue_NoState_IsFalse()
    {
        var ep = SeedEpisode();
        (await _service.ShouldOfferContinueAsync(ep.Id)).Should().BeFalse();
    }
}

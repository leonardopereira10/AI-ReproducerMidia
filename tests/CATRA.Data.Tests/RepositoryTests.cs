using CATRA.Core.Enums;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using FluentAssertions;
using Xunit;

namespace CATRA.Data.Tests;

/// <summary>
/// Integration tests for the SQLite data layer. Each test uses an isolated
/// temporary database file — never the real <c>%AppData%</c> database.
/// </summary>
public sealed class RepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly DatabaseConnection _database;

    public RepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "catra-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = new DatabaseConnection(Path.Combine(_directory, "test.db"));
        new DatabaseInitializer(_database).Initialize();
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup; WAL files may linger briefly on Windows.
        }
    }

    private CategoryRepository Categories => new(_database);
    private MediaItemRepository MediaItems => new(_database);
    private EpisodeRepository Episodes => new(_database);
    private ProcessedFileRepository ProcessedFiles => new(_database);
    private ProcessJobRepository ProcessJobs => new(_database);
    private WatchStateRepository WatchStates => new(_database);
    private AppSettingsRepository Settings => new(_database);

    private Category NewCategory(string name = "Animes") =>
        Categories.Insert(new Category { Name = name, FolderPath = @"C:\" + name });

    private MediaItem NewMediaItem(int categoryId, string title = "Series A", MediaType type = MediaType.Series) =>
        MediaItems.Insert(new MediaItem
        {
            CategoryId = categoryId,
            Title = title,
            RawFolderName = title,
            FolderPath = @"C:\" + title,
            MediaType = type,
        });

    private Episode NewEpisode(int mediaItemId, int? number, string? path = null) =>
        Episodes.Insert(new Episode
        {
            MediaItemId = mediaItemId,
            FileName = $"ep{number}.mp4",
            FilePath = path ?? $"C:\\vid\\{Guid.NewGuid():N}.mp4",
            EpisodeNumber = number,
        });

    // ── Schema / connection ────────────────────────────────────────────────

    [Fact]
    public void Initialize_CreatesAllSevenTables()
    {
        var tables = _database.Connection
            .QueryScalars<string>("SELECT name FROM sqlite_master WHERE type='table';");

        tables.Should().Contain(new[]
        {
            "Category", "MediaItem", "Episode", "ProcessedFile",
            "ProcessJob", "WatchState", "AppSettings",
        });
    }

    [Fact]
    public void Initialize_EnablesWalJournalMode()
    {
        var mode = _database.Connection.ExecuteScalar<string>("PRAGMA journal_mode;");
        mode.Should().BeEquivalentTo("wal");
    }

    [Fact]
    public void Initialize_SetsSchemaVersion()
    {
        var version = _database.Connection.ExecuteScalar<int>("PRAGMA user_version;");
        version.Should().Be(DatabaseInitializer.CurrentSchemaVersion);
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        var act = () => new DatabaseInitializer(_database).Initialize();
        act.Should().NotThrow();
        Settings.Get("window_size").Should().Be("5");
    }

    // ── AppSettings ────────────────────────────────────────────────────────

    [Fact]
    public void Settings_SeedsAllSpecDefaults()
    {
        var all = Settings.GetAll();

        all.Should().ContainKeys(
            "root_folder", "processed_folder", "window_size", "cleanup_on_close",
            "local_target_fps", "local_target_width", "local_target_height", "local_encode_bitrate_kbps",
            "dlna_target_fps", "dlna_target_width", "dlna_target_height", "dlna_encode_bitrate_kbps",
            "interp_method", "upscale_method", "default_skip_intro_sec", "theme_override");

        all["local_target_fps"].Should().Be("135");
        all["dlna_target_height"].Should().Be("2160");
        all["interp_method"].Should().Be("rife");
        all["upscale_method"].Should().Be("fsr4");
    }

    [Fact]
    public void Settings_SetOverwrites_AndGetReturnsValue()
    {
        Settings.Set("window_size", "7");
        Settings.Get("window_size").Should().Be("7");

        Settings.Set("custom_key", "custom_value");
        Settings.Get("custom_key").Should().Be("custom_value");
    }

    [Fact]
    public void Settings_GetMissing_ReturnsNull()
        => Settings.Get("does_not_exist").Should().BeNull();

    // ── Category ───────────────────────────────────────────────────────────

    [Fact]
    public void Category_CrudRoundTrip()
    {
        var category = NewCategory("Filmes");
        category.Id.Should().BeGreaterThan(0);

        var fetched = Categories.GetById(category.Id);
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Filmes");

        fetched.FolderPath = @"D:\Filmes";
        Categories.Update(fetched).Should().BeTrue();
        Categories.GetById(category.Id)!.FolderPath.Should().Be(@"D:\Filmes");

        Categories.Delete(category.Id).Should().BeTrue();
        Categories.GetById(category.Id).Should().BeNull();
    }

    [Fact]
    public void Category_GetByName_ReturnsMatch()
    {
        NewCategory("Animes");
        Categories.GetByName("Animes").Should().NotBeNull();
        Categories.GetByName("Missing").Should().BeNull();
    }

    // ── MediaItem ──────────────────────────────────────────────────────────

    [Fact]
    public void MediaItem_CrudAndEnumRoundTrip()
    {
        var category = NewCategory();
        var item = NewMediaItem(category.Id, "Movie X", MediaType.Movie);

        MediaItems.GetById(item.Id)!.MediaType.Should().Be(MediaType.Movie);

        item.Title = "Renamed";
        MediaItems.Update(item).Should().BeTrue();
        MediaItems.GetById(item.Id)!.Title.Should().Be("Renamed");

        MediaItems.Delete(item.Id).Should().BeTrue();
        MediaItems.GetById(item.Id).Should().BeNull();
    }

    [Fact]
    public void MediaItem_GetByCategory_FiltersCorrectly()
    {
        var c1 = NewCategory("A");
        var c2 = NewCategory("B");
        NewMediaItem(c1.Id, "S1");
        NewMediaItem(c1.Id, "S2");
        NewMediaItem(c2.Id, "S3");

        MediaItems.GetByCategory(c1.Id).Should().HaveCount(2);
        MediaItems.GetByCategory(c2.Id).Should().HaveCount(1);
    }

    // ── Episode ────────────────────────────────────────────────────────────

    [Fact]
    public void Episode_CrudRoundTrip()
    {
        var item = NewMediaItem(NewCategory().Id);
        var episode = NewEpisode(item.Id, 1);

        Episodes.GetById(episode.Id)!.FileName.Should().Be("ep1.mp4");

        episode.DisplayTitle = "Pilot";
        Episodes.Update(episode).Should().BeTrue();
        Episodes.GetById(episode.Id)!.DisplayTitle.Should().Be("Pilot");

        Episodes.Delete(episode.Id).Should().BeTrue();
        Episodes.GetById(episode.Id).Should().BeNull();
    }

    [Fact]
    public void Episode_GetByMediaItem_OrdersByEpisodeNumber()
    {
        var item = NewMediaItem(NewCategory().Id);
        NewEpisode(item.Id, 3);
        NewEpisode(item.Id, 1);
        NewEpisode(item.Id, 2);

        var episodes = Episodes.GetByMediaItem(item.Id);
        episodes.Select(e => e.EpisodeNumber).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public void Episode_GetUnwatched_ExcludesWatched_AndOrdersByNumber()
    {
        var item = NewMediaItem(NewCategory().Id);
        var ep1 = NewEpisode(item.Id, 1);
        var ep2 = NewEpisode(item.Id, 2);
        var ep3 = NewEpisode(item.Id, 3);

        // Mark ep1 watched.
        WatchStates.Insert(new WatchState { EpisodeId = ep1.Id, Watched = true, ProgressPct = 100 });
        // ep2 has in-progress state (not watched) → still unwatched.
        WatchStates.Insert(new WatchState { EpisodeId = ep2.Id, Watched = false, ProgressPct = 40 });

        var unwatched = Episodes.GetUnwatchedByMediaItem(item.Id);
        unwatched.Select(e => e.Id).Should().BeEquivalentTo(new[] { ep2.Id, ep3.Id });
        unwatched.Select(e => e.EpisodeNumber).Should().ContainInOrder(2, 3);
    }

    // ── ProcessedFile ──────────────────────────────────────────────────────

    [Fact]
    public void ProcessedFile_CrudAndByEpisodeProfile()
    {
        var episode = NewEpisode(NewMediaItem(NewCategory().Id).Id, 1);
        var file = ProcessedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episode.Id,
            Profile = ProcessProfile.Local,
            FilePath = @"C:\processed\1_local.mp4",
            TargetFps = 135,
            TargetWidth = 1920,
            TargetHeight = 1080,
            SourceHash = "abc",
        });

        ProcessedFiles.GetByEpisodeAndProfile(episode.Id, ProcessProfile.Local)!.Id.Should().Be(file.Id);
        ProcessedFiles.GetByEpisodeAndProfile(episode.Id, ProcessProfile.Dlna).Should().BeNull();

        file.FileSizeBytes = 12345;
        ProcessedFiles.Update(file).Should().BeTrue();
        ProcessedFiles.GetById(file.Id)!.FileSizeBytes.Should().Be(12345);

        ProcessedFiles.Delete(file.Id).Should().BeTrue();
    }

    [Fact]
    public void ProcessedFile_UniqueEpisodeProfile_IsEnforced()
    {
        var episode = NewEpisode(NewMediaItem(NewCategory().Id).Id, 1);
        ProcessedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episode.Id,
            Profile = ProcessProfile.Dlna,
            FilePath = "a.mp4",
            TargetFps = 55,
            TargetWidth = 3840,
            TargetHeight = 2160,
            SourceHash = "h",
        });

        var duplicate = () => ProcessedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episode.Id,
            Profile = ProcessProfile.Dlna,
            FilePath = "b.mp4",
            TargetFps = 55,
            TargetWidth = 3840,
            TargetHeight = 2160,
            SourceHash = "h",
        });

        duplicate.Should().Throw<Exception>();
    }

    // ── ProcessJob ─────────────────────────────────────────────────────────

    [Fact]
    public void ProcessJob_CrudAndEnumRoundTrip()
    {
        var episode = NewEpisode(NewMediaItem(NewCategory().Id).Id, 1);
        var job = ProcessJobs.Insert(new ProcessJob
        {
            EpisodeId = episode.Id,
            Profile = ProcessProfile.Local,
            Status = JobStatus.Queued,
            CurrentStep = ProcessStep.Decode,
        });

        var fetched = ProcessJobs.GetById(job.Id)!;
        fetched.Status.Should().Be(JobStatus.Queued);
        fetched.CurrentStep.Should().Be(ProcessStep.Decode);

        job.Status = JobStatus.Completed;
        job.CurrentStep = null;
        ProcessJobs.Update(job).Should().BeTrue();
        ProcessJobs.GetById(job.Id)!.Status.Should().Be(JobStatus.Completed);

        ProcessJobs.Delete(job.Id).Should().BeTrue();
    }

    [Fact]
    public void ProcessJob_GetActiveByMediaItem_ReturnsOnlyQueuedOrProcessing()
    {
        var item = NewMediaItem(NewCategory().Id);
        var e1 = NewEpisode(item.Id, 1);
        var e2 = NewEpisode(item.Id, 2);
        var e3 = NewEpisode(item.Id, 3);

        ProcessJobs.Insert(new ProcessJob { EpisodeId = e1.Id, Profile = ProcessProfile.Local, Status = JobStatus.Queued });
        ProcessJobs.Insert(new ProcessJob { EpisodeId = e2.Id, Profile = ProcessProfile.Local, Status = JobStatus.Processing });
        ProcessJobs.Insert(new ProcessJob { EpisodeId = e3.Id, Profile = ProcessProfile.Local, Status = JobStatus.Completed });

        var active = ProcessJobs.GetActiveByMediaItem(item.Id);
        active.Should().HaveCount(2);
        active.Select(j => j.Status).Should().OnlyContain(
            s => s == JobStatus.Queued || s == JobStatus.Processing);
    }

    // ── WatchState ─────────────────────────────────────────────────────────

    [Fact]
    public void WatchState_CrudAndByEpisodeId()
    {
        var episode = NewEpisode(NewMediaItem(NewCategory().Id).Id, 1);
        var state = WatchStates.Insert(new WatchState
        {
            EpisodeId = episode.Id,
            Watched = false,
            ProgressPct = 42,
            LastPositionSec = 500,
        });

        WatchStates.GetByEpisodeId(episode.Id)!.Id.Should().Be(state.Id);

        state.Watched = true;
        WatchStates.Update(state).Should().BeTrue();
        WatchStates.GetByEpisodeId(episode.Id)!.Watched.Should().BeTrue();

        WatchStates.Delete(state.Id).Should().BeTrue();
        WatchStates.GetByEpisodeId(episode.Id).Should().BeNull();
    }

    [Fact]
    public void WatchState_GetInProgress_ReturnsStartedButUnwatched()
    {
        var item = NewMediaItem(NewCategory().Id);
        var e1 = NewEpisode(item.Id, 1);
        var e2 = NewEpisode(item.Id, 2);
        var e3 = NewEpisode(item.Id, 3);

        WatchStates.Insert(new WatchState { EpisodeId = e1.Id, ProgressPct = 50, Watched = false }); // in progress
        WatchStates.Insert(new WatchState { EpisodeId = e2.Id, ProgressPct = 100, Watched = true }); // watched
        WatchStates.Insert(new WatchState { EpisodeId = e3.Id, ProgressPct = 0, Watched = false }); // not started

        var inProgress = WatchStates.GetInProgress();
        inProgress.Should().HaveCount(1);
        inProgress.Single().EpisodeId.Should().Be(e1.Id);
    }
}

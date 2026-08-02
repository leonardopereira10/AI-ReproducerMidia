using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Storage;
using CATRA.Services.Tests.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Storage;

/// <summary>
/// ST-21 / RF-04 / RN-10 tests for <see cref="CleanupService"/>: real repositories over
/// a temporary SQLite database and a temporary processed folder, a recording fake queue
/// and a controllable in-use probe. Covers shutdown cleanup (CleanupAllAsync), startup
/// crash recovery (CleanupOrphansAsync), per-episode deletion, folder sizing and the
/// "never delete an in-use/locked file" guarantees.
/// </summary>
public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly string _processedFolder;
    private readonly DatabaseConnection _database;
    private readonly ProcessedFileRepository _processedFiles;
    private readonly ProcessJobRepository _jobs;
    private readonly AppSettingsRepository _settings;
    private readonly FakeProcessingQueue _queue;
    private readonly FakeProcessedFileUsage _usage;
    private readonly CleanupService _service;

    public CleanupServiceTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "catra-st21-cleanup-" + Guid.NewGuid().ToString("N"));
        _processedFolder = Path.Combine(_workDirectory, "processed");
        Directory.CreateDirectory(_processedFolder);

        _database = new DatabaseConnection(Path.Combine(_workDirectory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _processedFiles = new ProcessedFileRepository(_database);
        _jobs = new ProcessJobRepository(_database);
        _settings = new AppSettingsRepository(_database);
        _settings.Set(AppSettingsModel.ProcessedFolderKey, _processedFolder);

        _queue = new FakeProcessingQueue();
        _usage = new FakeProcessedFileUsage();

        // Shrink the delete-retry delay so the locked-file tests stay fast.
        _service = new CleanupService(
            _processedFiles, _jobs, _settings, _queue, _usage,
            maxDeleteRetries: 3, deleteRetryDelay: TimeSpan.FromMilliseconds(10));
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

    /// <summary>Creates a real processed file on disk plus its DB record.</summary>
    private ProcessedFile SeedProcessedFile(int episodeId, ProcessProfile profile, string content = "processed-bytes")
    {
        string path = Path.Combine(_processedFolder, $"{episodeId}_{profile}.mp4");
        File.WriteAllText(path, content);
        return _processedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episodeId,
            Profile = profile,
            FilePath = path,
            FileSizeBytes = new FileInfo(path).Length,
            TargetFps = 135,
            TargetWidth = 1920,
            TargetHeight = 1080,
        });
    }

    private ProcessJob SeedJob(int episodeId, ProcessProfile profile, JobStatus status) =>
        _jobs.Insert(new ProcessJob
        {
            EpisodeId = episodeId,
            Profile = profile,
            Status = status,
        });

    // ── contract ───────────────────────────────────────────────────────────

    [Fact]
    public void Service_ImplementsICleanupService() =>
        typeof(CleanupService).Should().Implement<ICleanupService>();

    [Fact]
    public void Constructor_NullRequiredDependencies_Throws()
    {
        Action processedFiles = () => _ = new CleanupService(null!, _jobs, _settings);
        Action jobs = () => _ = new CleanupService(_processedFiles, null!, _settings);
        Action settings = () => _ = new CleanupService(_processedFiles, _jobs, null!);

        processedFiles.Should().Throw<ArgumentNullException>();
        jobs.Should().Throw<ArgumentNullException>();
        settings.Should().Throw<ArgumentNullException>();
    }

    // ── CleanupAllAsync (shutdown) ─────────────────────────────────────────

    [Fact]
    public async Task CleanupAll_DeletesEveryFile_ClearsBothTables()
    {
        ProcessedFile a = SeedProcessedFile(1, ProcessProfile.Local);
        ProcessedFile b = SeedProcessedFile(2, ProcessProfile.Dlna);
        SeedJob(1, ProcessProfile.Local, JobStatus.Completed);
        SeedJob(3, ProcessProfile.Local, JobStatus.Queued);

        CleanupResult result = await _service.CleanupAllAsync();

        result.FilesDeleted.Should().Be(2);
        result.BytesFreed.Should().Be(a.FileSizeBytes!.Value + b.FileSizeBytes!.Value);
        result.Errors.Should().BeEmpty();
        File.Exists(a.FilePath).Should().BeFalse();
        File.Exists(b.FilePath).Should().BeFalse();
        _processedFiles.GetAll().Should().BeEmpty();
        _jobs.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAll_CancelsActiveJob()
    {
        await _service.CleanupAllAsync();

        _queue.CancelCurrentCount.Should().Be(1, "the active job is cancelled before deleting files");
    }

    [Fact]
    public async Task CleanupAll_FileReportedInUse_IsSkipped_AndReported()
    {
        ProcessedFile inUse = SeedProcessedFile(1, ProcessProfile.Local);
        ProcessedFile free = SeedProcessedFile(2, ProcessProfile.Local);
        _usage.SetInUse(inUse.FilePath);

        CleanupResult result = await _service.CleanupAllAsync();

        File.Exists(inUse.FilePath).Should().BeTrue("an in-use file is never deleted");
        File.Exists(free.FilePath).Should().BeFalse();
        result.FilesDeleted.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain(inUse.FilePath);

        // Tables are cleared regardless; the skipped file becomes a startup orphan.
        _processedFiles.GetAll().Should().BeEmpty();
        _jobs.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAll_FileLockedByPlayer_IsRetried_AndReported()
    {
        ProcessedFile locked = SeedProcessedFile(1, ProcessProfile.Local);

        // Hold a handle without FileShare.Delete so File.Delete throws IOException.
        await using (var lockStream = new FileStream(
                         locked.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CleanupResult result = await _service.CleanupAllAsync();

            File.Exists(locked.FilePath).Should().BeTrue("a locked file survives the delete retries");
            result.FilesDeleted.Should().Be(0);
            result.Errors.Should().ContainSingle().Which.Should().Contain(locked.FilePath);
        }
    }

    [Fact]
    public async Task CleanupAll_RecordWithoutFile_StillClearsTables_NoError()
    {
        ProcessedFile ghost = SeedProcessedFile(1, ProcessProfile.Local);
        File.Delete(ghost.FilePath); // record exists, file already gone

        CleanupResult result = await _service.CleanupAllAsync();

        result.Errors.Should().BeEmpty();
        result.FilesDeleted.Should().Be(0);
        _processedFiles.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAll_EmptyDatabase_Succeeds()
    {
        CleanupResult result = await _service.CleanupAllAsync();

        result.FilesDeleted.Should().Be(0);
        result.BytesFreed.Should().Be(0);
        result.Errors.Should().BeEmpty();
    }

    // ── CleanupOrphansAsync (startup crash recovery) ───────────────────────

    [Fact]
    public async Task CleanupOrphans_DeletesUnregisteredFiles_KeepsRegistered()
    {
        ProcessedFile registered = SeedProcessedFile(1, ProcessProfile.Local);
        string orphan = Path.Combine(_processedFolder, "999_local.mp4");
        File.WriteAllText(orphan, "orphan-bytes");

        CleanupResult result = await _service.CleanupOrphansAsync();

        File.Exists(orphan).Should().BeFalse("an orphan file has no DB record");
        File.Exists(registered.FilePath).Should().BeTrue("a registered file is kept");
        result.FilesDeleted.Should().Be(1);
        result.BytesFreed.Should().Be(12); // "orphan-bytes"
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupOrphans_RemovesStaleRecords()
    {
        ProcessedFile ghost = SeedProcessedFile(1, ProcessProfile.Local);
        File.Delete(ghost.FilePath); // file gone, record left behind by a crash

        await _service.CleanupOrphansAsync();

        _processedFiles.GetById(ghost.Id).Should().BeNull("a record without a file is pruned");
    }

    [Fact]
    public async Task CleanupOrphans_InUseOrphan_IsSkipped()
    {
        string orphan = Path.Combine(_processedFolder, "999_local.mp4");
        File.WriteAllText(orphan, "orphan-bytes");
        _usage.SetInUse(orphan);

        CleanupResult result = await _service.CleanupOrphansAsync();

        File.Exists(orphan).Should().BeTrue("an in-use orphan is never deleted");
        result.FilesDeleted.Should().Be(0);
        result.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task CleanupOrphans_MissingFolder_DoesNotThrow()
    {
        _settings.Set(AppSettingsModel.ProcessedFolderKey, Path.Combine(_workDirectory, "does-not-exist"));

        CleanupResult result = await _service.CleanupOrphansAsync();

        result.FilesDeleted.Should().Be(0);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupOrphans_IgnoresNonMp4Files()
    {
        string txt = Path.Combine(_processedFolder, "notes.txt");
        File.WriteAllText(txt, "not a video");

        CleanupResult result = await _service.CleanupOrphansAsync();

        File.Exists(txt).Should().BeTrue("only *.mp4 orphans are reclaimed");
        result.FilesDeleted.Should().Be(0);
    }

    // ── DeleteProcessedAsync (per episode) ─────────────────────────────────

    [Fact]
    public async Task DeleteProcessed_DeletesOnlyTheEpisodeFiles()
    {
        ProcessedFile target = SeedProcessedFile(1, ProcessProfile.Local);
        ProcessedFile other = SeedProcessedFile(2, ProcessProfile.Local);

        await _service.DeleteProcessedAsync(1);

        File.Exists(target.FilePath).Should().BeFalse();
        _processedFiles.GetById(target.Id).Should().BeNull();
        File.Exists(other.FilePath).Should().BeTrue("other episodes are untouched");
        _processedFiles.GetById(other.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteProcessed_InUseFile_KeepsRecord()
    {
        ProcessedFile target = SeedProcessedFile(1, ProcessProfile.Local);
        _usage.SetInUse(target.FilePath);

        await _service.DeleteProcessedAsync(1);

        File.Exists(target.FilePath).Should().BeTrue("an in-use file is never deleted");
        _processedFiles.GetById(target.Id).Should().NotBeNull("its record is kept so it is not orphaned");
    }

    // ── GetProcessedFolderSizeBytes ────────────────────────────────────────

    [Fact]
    public void GetProcessedFolderSizeBytes_SumsFolderFiles()
    {
        SeedProcessedFile(1, ProcessProfile.Local, content: "0123456789"); // 10 bytes
        SeedProcessedFile(2, ProcessProfile.Local, content: "01234");        // 5 bytes

        _service.GetProcessedFolderSizeBytes().Should().Be(15);
    }

    [Fact]
    public void GetProcessedFolderSizeBytes_MissingFolder_ReturnsZero()
    {
        _settings.Set(AppSettingsModel.ProcessedFolderKey, Path.Combine(_workDirectory, "does-not-exist"));

        _service.GetProcessedFolderSizeBytes().Should().Be(0);
    }

    // ── concurrency ────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentCleanups_AreSerialized_DoNotThrow()
    {
        SeedProcessedFile(1, ProcessProfile.Local);
        SeedProcessedFile(2, ProcessProfile.Dlna);

        // Both calls contend for the same semaphore; an unsynchronized
        // implementation would surface as duplicate deletes or exceptions.
        await Task.WhenAll(_service.CleanupAllAsync(), _service.CleanupOrphansAsync());

        _processedFiles.GetAll().Should().BeEmpty();
    }
}

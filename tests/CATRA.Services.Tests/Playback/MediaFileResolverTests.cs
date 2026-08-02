using CATRA.Core.Enums;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Playback;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Playback;

/// <summary>
/// Integration tests for <see cref="MediaFileResolver"/> (ST-20, RF-05/RF-06,
/// RN-09) against a real temporary SQLite database (Episode + ProcessedFile
/// repositories) and real temporary files on disk (so <see cref="File.Exists"/>
/// is exercised genuinely). Covers: processed available → used; processed absent
/// → original; stale source hash → original fallback; processed record pointing
/// at a missing file → original fallback; and the per-profile display labels.
/// </summary>
public sealed class MediaFileResolverTests : IDisposable
{
    private const string SourceHash = "sha256-original";

    private readonly string _directory;
    private readonly DatabaseConnection _database;
    private readonly EpisodeRepository _episodes;
    private readonly ProcessedFileRepository _processedFiles;
    private readonly MediaFileResolver _resolver;
    private readonly List<string> _tempFiles = new();

    public MediaFileResolverTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "catra-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = new DatabaseConnection(Path.Combine(_directory, "test.db"));
        new DatabaseInitializer(_database).Initialize();

        _episodes = new EpisodeRepository(_database);
        _processedFiles = new ProcessedFileRepository(_database);
        _resolver = new MediaFileResolver(_processedFiles, _episodes);
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
            // Best effort: WAL files may linger briefly on Windows.
        }
    }

    private Episode NewEpisode(string? fileHash = SourceHash)
    {
        var category = new CategoryRepository(_database).Insert(new Category
        {
            Name = "Animes",
            FolderPath = Path.Combine(_directory, "Animes"),
        });
        var mediaItem = new MediaItemRepository(_database).Insert(new MediaItem
        {
            CategoryId = category.Id,
            Title = "Serie",
            RawFolderName = "Serie",
            FolderPath = Path.Combine(_directory, "Serie"),
            MediaType = MediaType.Series,
        });
        return _episodes.Insert(new Episode
        {
            MediaItemId = mediaItem.Id,
            FileName = "s01e01.mkv",
            FilePath = Path.Combine(_directory, "s01e01.mkv"),
            EpisodeNumber = 1,
            FileHash = fileHash,
        });
    }

    /// <summary>Creates a real file on disk and returns its path.</summary>
    private string NewExistingFile(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[16]);
        _tempFiles.Add(path);
        return path;
    }

    private ProcessedFile NewProcessedFile(int episodeId, ProcessProfile profile, string filePath, string sourceHash)
        => _processedFiles.Insert(new ProcessedFile
        {
            EpisodeId = episodeId,
            Profile = profile,
            FilePath = filePath,
            SourceHash = sourceHash,
            TargetFps = profile == ProcessProfile.Local ? 135 : 55,
            TargetHeight = profile == ProcessProfile.Local ? 1080 : 2160,
        });

    // ── processed available → used ────────────────────────────────────────

    [Fact]
    public async Task Resolve_LocalProcessedAvailable_UsesProcessedFile()
    {
        var episode = NewEpisode();
        string processedPath = NewExistingFile("s01e01_local.mp4");
        NewProcessedFile(episode.Id, ProcessProfile.Local, processedPath, SourceHash);

        var resolved = await _resolver.ResolveAsync(episode.Id, ProcessProfile.Local);

        resolved.IsProcessed.Should().BeTrue();
        resolved.FilePath.Should().Be(processedPath);
        resolved.Profile.Should().Be(ProcessProfile.Local);
        resolved.DisplayLabel.Should().Be(MediaFileResolver.LocalLabel);
        resolved.FellBackFromProcessed.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_DlnaProcessedAvailable_UsesDlnaLabel()
    {
        var episode = NewEpisode();
        string processedPath = NewExistingFile("s01e01_dlna.mp4");
        NewProcessedFile(episode.Id, ProcessProfile.Dlna, processedPath, SourceHash);

        var resolved = await _resolver.ResolveAsync(episode.Id, ProcessProfile.Dlna);

        resolved.IsProcessed.Should().BeTrue();
        resolved.FilePath.Should().Be(processedPath);
        resolved.Profile.Should().Be(ProcessProfile.Dlna);
        resolved.DisplayLabel.Should().Be(MediaFileResolver.DlnaLabel);
    }

    [Fact]
    public async Task Resolve_ProfileIsolation_OnlyMatchingProfileUsed()
    {
        // A Dlna processed file must not satisfy a Local resolve.
        var episode = NewEpisode();
        string dlnaPath = NewExistingFile("s01e01_dlna.mp4");
        NewProcessedFile(episode.Id, ProcessProfile.Dlna, dlnaPath, SourceHash);

        var resolved = await _resolver.ResolveAsync(episode.Id, ProcessProfile.Local);

        resolved.IsProcessed.Should().BeFalse();
        resolved.FilePath.Should().Be(episode.FilePath);
        resolved.FellBackFromProcessed.Should().BeFalse("no Local record exists, so this is a plain never-processed episode");
    }

    // ── processed absent → original ───────────────────────────────────────

    [Fact]
    public async Task Resolve_NoProcessedFile_FallsBackToOriginal()
    {
        var episode = NewEpisode();

        var resolved = await _resolver.ResolveAsync(episode.Id, ProcessProfile.Local);

        resolved.IsProcessed.Should().BeFalse();
        resolved.FilePath.Should().Be(episode.FilePath);
        resolved.Profile.Should().BeNull();
        resolved.DisplayLabel.Should().Be(MediaFileResolver.OriginalLabel);
        resolved.FellBackFromProcessed.Should().BeFalse();
    }

    // ── RN-09 stale detection → original ──────────────────────────────────

    [Fact]
    public async Task Resolve_StaleSourceHash_FallsBackToOriginal_AndMarksFallback()
    {
        var episode = NewEpisode(fileHash: "sha256-CHANGED");
        string processedPath = NewExistingFile("s01e01_local.mp4");
        // Processed output was built from a different (older) source hash.
        NewProcessedFile(episode.Id, ProcessProfile.Local, processedPath, SourceHash);

        var resolved = await _resolver.ResolveAsync(episode.Id, ProcessProfile.Local);

        resolved.IsProcessed.Should().BeFalse();
        resolved.FilePath.Should().Be(episode.FilePath);
        resolved.Profile.Should().BeNull();
        resolved.DisplayLabel.Should().Be(MediaFileResolver.OriginalLabel);
        resolved.FellBackFromProcessed.Should().BeTrue("a processed record existed but is stale (RN-09)");
    }

    [Fact]
    public async Task Resolve_ProcessedRecordButFileMissing_FallsBackToOriginal_AndMarksFallback()
    {
        var episode = NewEpisode();
        string missingPath = Path.Combine(_directory, "deleted_local.mp4"); // never created
        NewProcessedFile(episode.Id, ProcessProfile.Local, missingPath, SourceHash);

        var resolved = await _resolver.ResolveAsync(episode.Id, ProcessProfile.Local);

        resolved.IsProcessed.Should().BeFalse();
        resolved.FilePath.Should().Be(episode.FilePath);
        resolved.FellBackFromProcessed.Should().BeTrue("a processed record existed but its file is gone");
    }

    // ── errors + labels ───────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_UnknownEpisode_Throws()
    {
        Func<Task> act = () => _resolver.ResolveAsync(9999, ProcessProfile.Local);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(ProcessProfile.Local, MediaFileResolver.LocalLabel)]
    [InlineData(ProcessProfile.Dlna, MediaFileResolver.DlnaLabel)]
    public void LabelFor_MapsFixedLabels(ProcessProfile profile, string expected)
        => MediaFileResolver.LabelFor(profile).Should().Be(expected);
}

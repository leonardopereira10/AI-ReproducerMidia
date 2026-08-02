using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="IProcessedFileRepository"/>.
/// </summary>
public sealed class ProcessedFileRepository : IProcessedFileRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public ProcessedFileRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessedFile> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<ProcessedFileEntity>()
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public ProcessedFile? GetById(int id)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection.Find<ProcessedFileEntity>(id);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public ProcessedFile? GetByEpisodeAndProfile(int episodeId, ProcessProfile profile)
    {
        var profileText = DbEnum.ToDb(profile);
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection
                .Table<ProcessedFileEntity>()
                .FirstOrDefault(p => p.EpisodeId == episodeId && p.Profile == profileText);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessedFile> GetByEpisode(int episodeId)
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<ProcessedFileEntity>()
                .Where(p => p.EpisodeId == episodeId)
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public ProcessedFile Insert(ProcessedFile entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_database.SyncRoot)
        {
            var row = ToEntity(entity);
            row.Id = 0;
            _database.Connection.Insert(row);
            entity.Id = row.Id;
            return entity;
        }
    }

    /// <inheritdoc />
    public bool Update(ProcessedFile entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_database.SyncRoot)
        {
            return _database.Connection.Update(ToEntity(entity)) > 0;
        }
    }

    /// <inheritdoc />
    public bool Delete(int id)
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection.Delete<ProcessedFileEntity>(id) > 0;
        }
    }

    private static ProcessedFile ToModel(ProcessedFileEntity e) => new()
    {
        Id = e.Id,
        EpisodeId = e.EpisodeId,
        Profile = DbEnum.FromDb<ProcessProfile>(e.Profile),
        FilePath = e.FilePath,
        FileSizeBytes = e.FileSizeBytes,
        TargetFps = e.TargetFps,
        TargetWidth = e.TargetWidth,
        TargetHeight = e.TargetHeight,
        EncodeBitrate = e.EncodeBitrate,
        InterpMethod = e.InterpMethod,
        UpscaleMethod = e.UpscaleMethod,
        ProcessedAt = e.ProcessedAt,
        SourceHash = e.SourceHash,
    };

    private static ProcessedFileEntity ToEntity(ProcessedFile m) => new()
    {
        Id = m.Id,
        EpisodeId = m.EpisodeId,
        Profile = DbEnum.ToDb(m.Profile),
        FilePath = m.FilePath,
        FileSizeBytes = m.FileSizeBytes,
        TargetFps = m.TargetFps,
        TargetWidth = m.TargetWidth,
        TargetHeight = m.TargetHeight,
        EncodeBitrate = m.EncodeBitrate,
        InterpMethod = m.InterpMethod,
        UpscaleMethod = m.UpscaleMethod,
        ProcessedAt = m.ProcessedAt,
        SourceHash = m.SourceHash,
    };
}

using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="IProcessJobRepository"/>.
/// </summary>
public sealed class ProcessJobRepository : IProcessJobRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public ProcessJobRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessJob> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<ProcessJobEntity>()
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public ProcessJob? GetById(int id)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection.Find<ProcessJobEntity>(id);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessJob> GetActiveByMediaItem(int mediaItemId)
    {
        const string sql =
            "SELECT j.* FROM ProcessJob j " +
            "INNER JOIN Episode e ON e.Id = j.EpisodeId " +
            "WHERE e.MediaItemId = ? AND j.Status IN ('queued', 'processing') " +
            "ORDER BY j.Priority DESC, j.CreatedAt ASC, j.Id ASC;";

        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Query<ProcessJobEntity>(sql, mediaItemId)
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public ProcessJob Insert(ProcessJob entity)
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
    public bool Update(ProcessJob entity)
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
            return _database.Connection.Delete<ProcessJobEntity>(id) > 0;
        }
    }

    private static ProcessJob ToModel(ProcessJobEntity e) => new()
    {
        Id = e.Id,
        EpisodeId = e.EpisodeId,
        Profile = DbEnum.FromDb<ProcessProfile>(e.Profile),
        Status = DbEnum.FromDb<JobStatus>(e.Status),
        Priority = e.Priority,
        ProgressPct = e.ProgressPct,
        CurrentStep = DbEnum.FromDbNullable<ProcessStep>(e.CurrentStep),
        ErrorMessage = e.ErrorMessage,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        CreatedAt = e.CreatedAt,
    };

    private static ProcessJobEntity ToEntity(ProcessJob m) => new()
    {
        Id = m.Id,
        EpisodeId = m.EpisodeId,
        Profile = DbEnum.ToDb(m.Profile),
        Status = DbEnum.ToDb(m.Status),
        Priority = m.Priority,
        ProgressPct = m.ProgressPct,
        CurrentStep = m.CurrentStep.HasValue ? DbEnum.ToDb(m.CurrentStep.Value) : null,
        ErrorMessage = m.ErrorMessage,
        StartedAt = m.StartedAt,
        CompletedAt = m.CompletedAt,
        CreatedAt = m.CreatedAt,
    };
}

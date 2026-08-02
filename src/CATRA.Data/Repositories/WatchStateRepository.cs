using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="IWatchStateRepository"/>.
/// </summary>
public sealed class WatchStateRepository : IWatchStateRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public WatchStateRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public IReadOnlyList<WatchState> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<WatchStateEntity>()
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public WatchState? GetById(int id)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection.Find<WatchStateEntity>(id);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public WatchState? GetByEpisodeId(int episodeId)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection
                .Table<WatchStateEntity>()
                .FirstOrDefault(w => w.EpisodeId == episodeId);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WatchState> GetInProgress()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<WatchStateEntity>()
                .Where(w => w.ProgressPct > 0 && !w.Watched)
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public WatchState Insert(WatchState entity)
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
    public bool Update(WatchState entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_database.SyncRoot)
        {
            entity.UpdatedAt = DateTime.UtcNow;
            return _database.Connection.Update(ToEntity(entity)) > 0;
        }
    }

    /// <inheritdoc />
    public bool Delete(int id)
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection.Delete<WatchStateEntity>(id) > 0;
        }
    }

    private static WatchState ToModel(WatchStateEntity e) => new()
    {
        Id = e.Id,
        EpisodeId = e.EpisodeId,
        Watched = e.Watched,
        ProgressPct = e.ProgressPct,
        LastPositionSec = e.LastPositionSec,
        UpdatedAt = e.UpdatedAt,
    };

    private static WatchStateEntity ToEntity(WatchState m) => new()
    {
        Id = m.Id,
        EpisodeId = m.EpisodeId,
        Watched = m.Watched,
        ProgressPct = m.ProgressPct,
        LastPositionSec = m.LastPositionSec,
        UpdatedAt = m.UpdatedAt,
    };
}

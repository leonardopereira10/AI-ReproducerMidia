using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="IEpisodeRepository"/>.
/// </summary>
public sealed class EpisodeRepository : IEpisodeRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public EpisodeRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public IReadOnlyList<Episode> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<EpisodeEntity>()
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public Episode? GetById(int id)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection.Find<EpisodeEntity>(id);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Episode> GetByMediaItem(int mediaItemId)
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<EpisodeEntity>()
                .Where(e => e.MediaItemId == mediaItemId)
                .OrderBy(e => e.EpisodeNumber)
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Episode> GetUnwatchedByMediaItem(int mediaItemId)
    {
        const string sql =
            "SELECT e.* FROM Episode e " +
            "LEFT JOIN WatchState w ON w.EpisodeId = e.Id " +
            "WHERE e.MediaItemId = ? AND (w.Watched IS NULL OR w.Watched = 0) " +
            "ORDER BY CASE WHEN e.EpisodeNumber IS NULL THEN 1 ELSE 0 END, " +
            "e.EpisodeNumber ASC, e.Id ASC;";

        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Query<EpisodeEntity>(sql, mediaItemId)
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public Episode? GetNextEpisode(int currentEpisodeId)
    {
        lock (_database.SyncRoot)
        {
            // 1. Busca o episódio atual para obter seu MediaItemId e EpisodeNumber
            var current = _database.Connection.Find<EpisodeEntity>(currentEpisodeId);
            if (current is null)
            {
                return null;
            }

            // 2. Busca o próximo episódio (EpisodeNumber > atual ou Id > atual se EpisodeNumber for nulo)
            const string sql =
                "SELECT * FROM Episode WHERE MediaItemId = ? AND (" +
                "  (EpisodeNumber IS NOT NULL AND ? IS NOT NULL AND EpisodeNumber > ?) OR" +
                "  (EpisodeNumber IS NULL AND ? IS NULL AND Id > ?)" +
                ") ORDER BY CASE WHEN EpisodeNumber IS NULL THEN 1 ELSE 0 END, " +
                "EpisodeNumber ASC, Id ASC LIMIT 1;";

            var next = _database.Connection
                .Query<EpisodeEntity>(sql, current.MediaItemId, current.EpisodeNumber, current.EpisodeNumber, current.EpisodeNumber, current.Id)
                .FirstOrDefault();

            return next is null ? null : ToModel(next);
        }
    }

    /// <inheritdoc />
    public Episode? GetPreviousEpisode(int currentEpisodeId)
    {
        lock (_database.SyncRoot)
        {
            // 1. Busca o episódio atual para obter seu MediaItemId e EpisodeNumber
            var current = _database.Connection.Find<EpisodeEntity>(currentEpisodeId);
            if (current is null)
            {
                return null;
            }

            // 2. Busca o episódio anterior (EpisodeNumber < atual ou Id < atual se EpisodeNumber for nulo)
            const string sql =
                "SELECT * FROM Episode WHERE MediaItemId = ? AND (" +
                "  (EpisodeNumber IS NOT NULL AND ? IS NOT NULL AND EpisodeNumber < ?) OR" +
                "  (EpisodeNumber IS NULL AND ? IS NULL AND Id < ?)" +
                ") ORDER BY CASE WHEN EpisodeNumber IS NULL THEN 1 ELSE 0 END, " +
                "EpisodeNumber DESC, Id DESC LIMIT 1;";

            var prev = _database.Connection
                .Query<EpisodeEntity>(sql, current.MediaItemId, current.EpisodeNumber, current.EpisodeNumber, current.EpisodeNumber, current.Id)
                .FirstOrDefault();

            return prev is null ? null : ToModel(prev);
        }
    }

    /// <inheritdoc />
    public Episode Insert(Episode entity)
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
    public bool Update(Episode entity)
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
            return _database.Connection.Delete<EpisodeEntity>(id) > 0;
        }
    }

    private static Episode ToModel(EpisodeEntity e) => new()
    {
        Id = e.Id,
        MediaItemId = e.MediaItemId,
        FileName = e.FileName,
        FilePath = e.FilePath,
        SeasonNumber = e.SeasonNumber,
        EpisodeNumber = e.EpisodeNumber,
        DisplayTitle = e.DisplayTitle,
        DurationSec = e.DurationSec,
        FileSizeBytes = e.FileSizeBytes,
        SourceFps = e.SourceFps,
        SourceWidth = e.SourceWidth,
        SourceHeight = e.SourceHeight,
        FileHash = e.FileHash,
        ThumbnailPath = e.ThumbnailPath,
        CreatedAt = e.CreatedAt,
    };

    private static EpisodeEntity ToEntity(Episode m) => new()
    {
        Id = m.Id,
        MediaItemId = m.MediaItemId,
        FileName = m.FileName,
        FilePath = m.FilePath,
        SeasonNumber = m.SeasonNumber,
        EpisodeNumber = m.EpisodeNumber,
        DisplayTitle = m.DisplayTitle,
        DurationSec = m.DurationSec,
        FileSizeBytes = m.FileSizeBytes,
        SourceFps = m.SourceFps,
        SourceWidth = m.SourceWidth,
        SourceHeight = m.SourceHeight,
        FileHash = m.FileHash,
        ThumbnailPath = m.ThumbnailPath,
        CreatedAt = m.CreatedAt,
    };
}

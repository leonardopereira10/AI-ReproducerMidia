using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="IMediaItemRepository"/>.
/// </summary>
public sealed class MediaItemRepository : IMediaItemRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public MediaItemRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaItem> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<MediaItemEntity>()
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public MediaItem? GetById(int id)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection.Find<MediaItemEntity>(id);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaItem> GetByCategory(int categoryId)
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<MediaItemEntity>()
                .Where(m => m.CategoryId == categoryId)
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public MediaItem Insert(MediaItem entity)
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
    public bool Update(MediaItem entity)
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
            return _database.Connection.Delete<MediaItemEntity>(id) > 0;
        }
    }

    private static MediaItem ToModel(MediaItemEntity e) => new()
    {
        Id = e.Id,
        CategoryId = e.CategoryId,
        Title = e.Title,
        RawFolderName = e.RawFolderName,
        FolderPath = e.FolderPath,
        MediaType = DbEnum.FromDb<MediaType>(e.MediaType),
        SkipIntroSec = e.SkipIntroSec,
        CoverPath = e.CoverPath,
        TmdbId = e.TmdbId,
        Synopsis = e.Synopsis,
        Year = e.Year,
        Genre = e.Genre,
        PosterUrl = e.PosterUrl,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };

    private static MediaItemEntity ToEntity(MediaItem m) => new()
    {
        Id = m.Id,
        CategoryId = m.CategoryId,
        Title = m.Title,
        RawFolderName = m.RawFolderName,
        FolderPath = m.FolderPath,
        MediaType = DbEnum.ToDb(m.MediaType),
        SkipIntroSec = m.SkipIntroSec,
        CoverPath = m.CoverPath,
        TmdbId = m.TmdbId,
        Synopsis = m.Synopsis,
        Year = m.Year,
        Genre = m.Genre,
        PosterUrl = m.PosterUrl,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };
}

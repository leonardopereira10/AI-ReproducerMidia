using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="ICategoryRepository"/>.
/// </summary>
public sealed class CategoryRepository : ICategoryRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public CategoryRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public IReadOnlyList<Category> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<CategoryEntity>()
                .ToList()
                .Select(ToModel)
                .ToList();
        }
    }

    /// <inheritdoc />
    public Category? GetById(int id)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection.Find<CategoryEntity>(id);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public Category? GetByName(string name)
    {
        lock (_database.SyncRoot)
        {
            var entity = _database.Connection
                .Table<CategoryEntity>()
                .FirstOrDefault(c => c.Name == name);
            return entity is null ? null : ToModel(entity);
        }
    }

    /// <inheritdoc />
    public Category Insert(Category entity)
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
    public bool Update(Category entity)
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
            return _database.Connection.Delete<CategoryEntity>(id) > 0;
        }
    }

    private static Category ToModel(CategoryEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        FolderPath = e.FolderPath,
        CreatedAt = e.CreatedAt,
    };

    private static CategoryEntity ToEntity(Category m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        FolderPath = m.FolderPath,
        CreatedAt = m.CreatedAt,
    };
}

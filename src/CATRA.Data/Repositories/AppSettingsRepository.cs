using CATRA.Core.Interfaces;
using CATRA.Data.Database;
using CATRA.Data.Entities;

namespace CATRA.Data.Repositories;

/// <summary>
/// SQLite-backed <see cref="IAppSettingsRepository"/>.
/// </summary>
public sealed class AppSettingsRepository : IAppSettingsRepository
{
    private readonly DatabaseConnection _database;

    /// <summary>Creates a repository bound to a connection.</summary>
    public AppSettingsRepository(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc />
    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_database.SyncRoot)
        {
            return _database.Connection.Find<AppSettingEntity>(key)?.Value;
        }
    }

    /// <inheritdoc />
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_database.SyncRoot)
        {
            _database.Connection.InsertOrReplace(new AppSettingEntity { Key = key, Value = value });
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetAll()
    {
        lock (_database.SyncRoot)
        {
            return _database.Connection
                .Table<AppSettingEntity>()
                .ToList()
                .ToDictionary(e => e.Key, e => e.Value);
        }
    }
}

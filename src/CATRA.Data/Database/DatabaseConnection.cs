using SQLite;

namespace CATRA.Data.Database;

/// <summary>
/// Owns the single SQLite connection used by all repositories.
/// Enables WAL journal mode and foreign keys on open.
/// </summary>
public sealed class DatabaseConnection : IDisposable
{
    private readonly Lazy<SQLiteConnection> _connection;
    private bool _disposed;

    /// <summary>
    /// Creates a factory for the database at <paramref name="databasePath"/>.
    /// Parent directories are created lazily on first connection use.
    /// </summary>
    public DatabaseConnection(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        _connection = new Lazy<SQLiteConnection>(() => Open(databasePath));
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Shared lock used by repositories to serialize access.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>The shared, lazily-opened connection.</summary>
    public SQLiteConnection Connection => _connection.Value;

    private static SQLiteConnection Open(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SQLiteConnection(
            databasePath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex,
            storeDateTimeAsTicks: false);

        // WAL mode for concurrent read/write performance (spec: non-functional requirement).
        // PRAGMA ... returns a result row, so read it via ExecuteScalar rather than Execute.
        connection.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");
        connection.ExecuteScalar<string>("PRAGMA foreign_keys=ON;");
        return connection;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }

        _disposed = true;
    }
}

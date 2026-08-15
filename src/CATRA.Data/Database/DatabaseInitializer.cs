using CATRA.Data.Entities;
using SQLite;

namespace CATRA.Data.Database;

/// <summary>
/// Creates and migrates the SQLite schema and seeds default settings.
/// Schema version is tracked with <c>PRAGMA user_version</c>.
/// </summary>
public sealed class DatabaseInitializer
{
    /// <summary>Current schema version applied by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly DatabaseConnection _database;

    /// <summary>Creates an initializer bound to a connection.</summary>
    public DatabaseInitializer(DatabaseConnection database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Applies all pending migrations and seeds default settings on first run.
    /// Safe to call on every startup.
    /// </summary>
    public void Initialize()
    {
        lock (_database.SyncRoot)
        {
            var connection = _database.Connection;
            var version = connection.ExecuteScalar<int>("PRAGMA user_version;");

            if (version < 1)
            {
                MigrateToV1(connection);
            }

            // Always ensure defaults exist (idempotent).
            SeedDefaultSettings(connection);
        }
    }

    private static void MigrateToV1(SQLiteConnection connection)
    {
        connection.CreateTable<CategoryEntity>();
        connection.CreateTable<MediaItemEntity>();
        connection.CreateTable<EpisodeEntity>();
        connection.CreateTable<ProcessedFileEntity>();
        connection.CreateTable<ProcessJobEntity>();
        connection.CreateTable<WatchStateEntity>();
        connection.CreateTable<AppSettingEntity>();

        connection.Execute($"PRAGMA user_version={CurrentSchemaVersion};");
    }

    private static void SeedDefaultSettings(SQLiteConnection connection)
    {
        foreach (var (key, value) in DefaultSettings)
        {
            // INSERT OR IGNORE keeps user-modified values intact.
            connection.Execute(
                "INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES (?, ?);",
                key,
                value);
        }
    }

    /// <summary>Default settings from the spec (key → value).</summary>
    public static IReadOnlyList<(string Key, string Value)> DefaultSettings { get; } = new[]
    {
        ("root_folder", string.Empty),
        ("processed_folder", string.Empty),
        ("window_size", "5"),
        ("cleanup_on_close", "true"),
        ("local_target_fps", "60"),
        ("local_target_width", "1920"),
        ("local_target_height", "1080"),
        ("local_encode_bitrate_kbps", "20000"),
        ("dlna_target_fps", "55"),
        ("dlna_target_width", "3840"),
        ("dlna_target_height", "2160"),
        ("dlna_encode_bitrate_kbps", "45000"),
        ("interp_method", "rife"),
        // D-PO-3: seed FSR 1 — without this the INSERT OR IGNORE seed keeps "fsr4"
        // on fresh installs and the FSR 1 default is never reached (plano Risco 3 / A6).
        ("upscale_method", "fsr1"),
        ("default_skip_intro_sec", "85"),
        ("theme_override", "system"),
    };
}

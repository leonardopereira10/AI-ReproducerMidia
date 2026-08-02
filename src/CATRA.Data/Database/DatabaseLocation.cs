namespace CATRA.Data.Database;

/// <summary>
/// Resolves the default database file location.
/// </summary>
public static class DatabaseLocation
{
    /// <summary>
    /// Default database path: <c>%AppData%/CATRA/catra.db</c>.
    /// Tests must never use this — pass an explicit temporary path instead.
    /// </summary>
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CATRA",
        "catra.db");
}

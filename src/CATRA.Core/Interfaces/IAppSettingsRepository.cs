namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for key/value application settings.
/// </summary>
public interface IAppSettingsRepository
{
    /// <summary>Returns the value for a key, or <c>null</c> if absent.</summary>
    string? Get(string key);

    /// <summary>Inserts or overwrites the value for a key.</summary>
    void Set(string key, string value);

    /// <summary>Returns every setting as key/value pairs.</summary>
    IReadOnlyDictionary<string, string> GetAll();
}

namespace CATRA.Data.Database;

/// <summary>
/// Maps enums to/from the lowercase text representation stored in SQLite.
/// </summary>
internal static class DbEnum
{
    /// <summary>Converts an enum to its lowercase database text.</summary>
    public static string ToDb<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => value.ToString().ToLowerInvariant();

    /// <summary>Parses database text into an enum (case-insensitive).</summary>
    public static TEnum FromDb<TEnum>(string? value)
        where TEnum : struct, Enum
        => string.IsNullOrWhiteSpace(value)
            ? default
            : Enum.Parse<TEnum>(value, ignoreCase: true);

    /// <summary>Parses nullable database text into a nullable enum.</summary>
    public static TEnum? FromDbNullable<TEnum>(string? value)
        where TEnum : struct, Enum
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.Parse<TEnum>(value, ignoreCase: true);
}

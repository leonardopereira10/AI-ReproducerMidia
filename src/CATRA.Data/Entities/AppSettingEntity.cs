using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>AppSettings</c> table.
/// </summary>
[Table("AppSettings")]
public sealed class AppSettingEntity
{
    /// <summary>Setting key (primary key).</summary>
    [PrimaryKey, Column("Key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Setting value.</summary>
    [NotNull, Column("Value")]
    public string Value { get; set; } = string.Empty;
}

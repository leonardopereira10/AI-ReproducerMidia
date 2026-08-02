using SQLite;

namespace CATRA.Data.Entities;

/// <summary>
/// SQLite row for the <c>Category</c> table.
/// </summary>
[Table("Category")]
public sealed class CategoryEntity
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, AutoIncrement, Column("Id")]
    public int Id { get; set; }

    /// <summary>Unique category name.</summary>
    [NotNull, Unique, Column("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Folder path.</summary>
    [NotNull, Column("FolderPath")]
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC).</summary>
    [NotNull, Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

namespace CATRA.Core.Models;

/// <summary>
/// Domain model for a top-level library category (level 1 of the folder tree).
/// </summary>
public sealed class Category
{
    /// <summary>Database identity. Zero when not yet persisted.</summary>
    public int Id { get; set; }

    /// <summary>Unique category name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute folder path backing this category.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

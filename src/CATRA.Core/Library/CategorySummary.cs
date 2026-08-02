using CATRA.Core.Models;

namespace CATRA.Core.Library;

/// <summary>
/// Read model for a library category plus the number of media items it
/// currently contains (Tela 1 — category tabs with counts).
/// </summary>
public sealed class CategorySummary
{
    /// <summary>Creates a summary wrapping a category and its item count.</summary>
    public CategorySummary(Category category, int mediaItemCount)
    {
        Category = category ?? throw new ArgumentNullException(nameof(category));
        MediaItemCount = mediaItemCount;
    }

    /// <summary>Underlying category.</summary>
    public Category Category { get; }

    /// <summary>Category database id.</summary>
    public int Id => Category.Id;

    /// <summary>Category display name.</summary>
    public string Name => Category.Name;

    /// <summary>Number of media items (series/movies) in this category.</summary>
    public int MediaItemCount { get; }
}

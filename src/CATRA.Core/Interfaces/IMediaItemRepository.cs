using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for <see cref="MediaItem"/> domain models.
/// </summary>
public interface IMediaItemRepository : IRepository<MediaItem>
{
    /// <summary>Returns all media items belonging to a category.</summary>
    IReadOnlyList<MediaItem> GetByCategory(int categoryId);
}

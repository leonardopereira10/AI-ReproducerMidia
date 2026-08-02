using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for <see cref="Category"/> domain models.
/// </summary>
public interface ICategoryRepository : IRepository<Category>
{
    /// <summary>Returns the category with the given unique name, or <c>null</c>.</summary>
    Category? GetByName(string name);
}

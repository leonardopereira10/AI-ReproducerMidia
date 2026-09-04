namespace CATRA.Core.Models;

/// <summary>
/// Immutable representation of a library category for the public API layer.
/// Maps from <see cref="Category"/>.
/// </summary>
public sealed record CategoryDto(
    int Id,
    string Name,
    string FolderPath,
    int ItemCount);
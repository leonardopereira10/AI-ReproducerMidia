using CATRA.Core.Enums;

namespace CATRA.Core.Models;

/// <summary>
/// Immutable representation of a series or movie for the public API layer.
/// Maps from <see cref="MediaItem"/>.
/// </summary>
public sealed record MediaItemDto(
    int Id,
    int CategoryId,
    string Title,
    MediaType MediaType,
    string? CoverPath,
    string? Synopsis,
    int? Year,
    string? Genre,
    string? PosterUrl);
using CATRA.Core.Enums;

namespace CATRA.Core.Models;

/// <summary>
/// Immutable result item produced by a library media search.
/// </summary>
public sealed record SearchResultDto(
    int Id,
    int CategoryId,
    string Title,
    MediaType MediaType,
    string? CoverPath,
    int? Year,
    string? Genre,
    string? PosterUrl);
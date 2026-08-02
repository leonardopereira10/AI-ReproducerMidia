namespace CATRA.Core.Enums;

/// <summary>
/// Kind of a <see cref="Models.MediaItem"/>. Persisted as lowercase text
/// (<c>series</c> / <c>movie</c>) in the <c>MediaItem.MediaType</c> column.
/// </summary>
public enum MediaType
{
    /// <summary>A series with one or more episodes.</summary>
    Series,

    /// <summary>A single-file movie.</summary>
    Movie,
}

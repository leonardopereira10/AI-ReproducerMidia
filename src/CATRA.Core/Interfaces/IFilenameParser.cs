using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Parses video file names into series/season/episode data (RF-02, four patterns).
/// </summary>
public interface IFilenameParser
{
    /// <summary>
    /// Parses <paramref name="fileName"/> (with or without extension).
    /// When no pattern matches, the result uses
    /// <see cref="Enums.FilenamePattern.Fallback"/> and carries the raw name.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is null/whitespace.</exception>
    FilenameParserResult Parse(string fileName);
}

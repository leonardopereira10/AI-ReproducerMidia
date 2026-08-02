using System.Globalization;
using System.Text.RegularExpressions;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// Regex-based file name parser implementing the four RF-02 patterns with
/// priority P1 &gt; P2 &gt; P3 &gt; P4 (fallback):
/// <list type="bullet">
/// <item>P1: <c>[Site][Name] - Episódio NN</c> → name + episode.</item>
/// <item>P2: <c>[Site] Name - Episódio NN (Quality)</c> → name + episode.</item>
/// <item>P3: <c>ABREV##EP##</c> → season + episode.</item>
/// <item>P4: fallback → raw name; the scanner adds folder name + container metadata.</item>
/// </list>
/// Extracted series names are normalized per RN-06 (<c>_</c> → <c>'</c>, title case).
/// </summary>
public sealed partial class FilenameParser : IFilenameParser
{
    [GeneratedRegex(@"^\[.*?\]\[(.+?)\]\s*-\s*Epis[óo]dio\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern1Regex();

    [GeneratedRegex(@"^\[.*?\]\s*(.+?)\s*-\s*Epis[óo]dio\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern2Regex();

    [GeneratedRegex(@"(\d{2})EP(\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern3Regex();

    /// <inheritdoc />
    public FilenameParserResult Parse(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var rawName = Path.GetFileNameWithoutExtension(fileName.Trim());

        var p1 = Pattern1Regex().Match(rawName);
        if (p1.Success)
        {
            return new FilenameParserResult
            {
                RawName = rawName,
                SeriesName = NameNormalizer.Normalize(p1.Groups[1].Value),
                EpisodeNumber = ParseNumber(p1.Groups[2].Value),
                PatternUsed = FilenamePattern.Pattern1,
            };
        }

        var p2 = Pattern2Regex().Match(rawName);
        if (p2.Success)
        {
            return new FilenameParserResult
            {
                RawName = rawName,
                SeriesName = NameNormalizer.Normalize(p2.Groups[1].Value),
                EpisodeNumber = ParseNumber(p2.Groups[2].Value),
                PatternUsed = FilenamePattern.Pattern2,
            };
        }

        var p3 = Pattern3Regex().Match(rawName);
        if (p3.Success)
        {
            return new FilenameParserResult
            {
                RawName = rawName,
                SeasonNumber = ParseNumber(p3.Groups[1].Value),
                EpisodeNumber = ParseNumber(p3.Groups[2].Value),
                PatternUsed = FilenamePattern.Pattern3,
            };
        }

        return new FilenameParserResult
        {
            RawName = rawName,
            PatternUsed = FilenamePattern.Fallback,
        };
    }

    private static int ParseNumber(string value) =>
        int.Parse(value, CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Text.RegularExpressions;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// Regex-based file name parser implementing the RF-02 patterns with
/// priority P1 &gt; P2 &gt; PublisherRelease &gt; P3 &gt; P4 (fallback):
/// <list type="bullet">
/// <item>P1: <c>[Site][Name] - Episódio NN</c> → name + episode.</item>
/// <item>P2: <c>[Site] Name - Episódio NN (Quality)</c> → name + episode.</item>
/// <item>PublisherRelease: <c>[Ep. NNN] Name - NT [Publisher] [Quality] [Lang]</c>
/// → name + season + episode + publisher.</item>
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

    /// <summary>
    /// Fansub/publisher release: <c>[Ep. 001] Martial Master - 1T [DonghuaNoSekai] [1080p] [PT-BR]</c>.
    /// Captures the leading episode tag, the series name, an optional <c>- NT</c>
    /// season marker and the trailing bracket tags (publisher / quality / language).
    /// </summary>
    [GeneratedRegex(
        @"^\[Ep\.?\s*(?<episode>\d+)\]\s*(?<name>.+?)\s*(?:-\s*(?<season>\d+)\s*T\b\s*)?(?<tags>(?:\s*\[[^\]]*\])+)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PublisherReleaseRegex();

    /// <summary>Resolution-style quality tags that are never the publisher.</summary>
    [GeneratedRegex(@"^\d{3,4}p$", RegexOptions.IgnoreCase)]
    private static partial Regex QualityResolutionRegex();

    /// <summary>Named quality tags that are never the publisher.</summary>
    [GeneratedRegex(@"^(4k|uhd|f?hd|sd|dvd|bd|bdrip|web-?rip|webdl)$", RegexOptions.IgnoreCase)]
    private static partial Regex QualityLabelRegex();

    /// <summary>Language tags (e.g. <c>PT-BR</c>, <c>EN</c>) that are never the publisher.</summary>
    [GeneratedRegex(@"^[a-z]{2,3}(?:-[a-z]{2,3})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageTagRegex();

    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"(\d{2})EP(\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern3Regex();

    /// <summary>
    /// A trailing real file extension (e.g. <c>.mp4</c>, <c>.mkv</c>). Used instead
    /// of <see cref="Path.GetFileNameWithoutExtension"/> so internal dots — such as
    /// the one in the <c>[Ep. 001]</c> tag — are preserved when the name carries no
    /// extension.
    /// </summary>
    [GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex ExtensionRegex();

    /// <inheritdoc />
    public FilenameParserResult Parse(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var rawName = StripExtension(Path.GetFileName(fileName.Trim()));

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

        var release = PublisherReleaseRegex().Match(rawName);
        if (release.Success)
        {
            var seasonGroup = release.Groups["season"];
            return new FilenameParserResult
            {
                RawName = rawName,
                SeriesName = NameNormalizer.Normalize(release.Groups["name"].Value),
                SeasonNumber = seasonGroup.Success ? ParseNumber(seasonGroup.Value) : null,
                EpisodeNumber = ParseNumber(release.Groups["episode"].Value),
                Publisher = ExtractPublisher(release.Groups["tags"].Value),
                PatternUsed = FilenamePattern.PublisherRelease,
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

    /// <summary>
    /// Removes a trailing file extension (dot + short alphanumeric segment) without
    /// touching internal dots, so names like <c>[Ep. 001] ...</c> survive intact.
    /// </summary>
    private static string StripExtension(string fileNameOnly) =>
        ExtensionRegex().Replace(fileNameOnly, string.Empty);

    /// <summary>
    /// Returns the first trailing bracket tag that is not a quality or language
    /// tag, i.e. the release group / publisher (<c>DonghuaNoSekai</c>). Casing is
    /// preserved. Returns <c>null</c> when no such tag exists.
    /// </summary>
    private static string? ExtractPublisher(string tags)
    {
        foreach (Match match in BracketTagRegex().Matches(tags))
        {
            string content = match.Groups[1].Value.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            if (QualityResolutionRegex().IsMatch(content) ||
                QualityLabelRegex().IsMatch(content) ||
                LanguageTagRegex().IsMatch(content))
            {
                continue;
            }

            return content;
        }

        return null;
    }
}

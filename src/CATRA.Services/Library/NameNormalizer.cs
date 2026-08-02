using System.Text.RegularExpressions;

namespace CATRA.Services.Library;

/// <summary>
/// Name normalization rules RN-06:
/// <list type="bullet">
/// <item><c>_</c> becomes a possessive apostrophe (<c>Mortal_s</c> → <c>Mortal's</c>).</item>
/// <item><c>[Site]</c> tags are removed from display names.</item>
/// <item>Display names use title case (small words such as <c>of</c>/<c>a</c>/<c>no</c>
/// stay lowercase unless first/last).</item>
/// </list>
/// The scanner keeps the raw folder name intact on <c>MediaItem.RawFolderName</c>;
/// this class only produces display-ready strings.
/// </summary>
public static partial class NameNormalizer
{
    /// <summary>
    /// Words kept lowercase in title case (articles, short conjunctions/prepositions,
    /// plus the Japanese particle <c>no</c> common in anime titles).
    /// </summary>
    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by",
        "da", "das", "de", "do", "dos",
        "for", "from", "in",
        "la", "le", "les",
        "no", "nor",
        "of", "on", "or",
        "the", "to", "via",
    };

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Applies every RN-06 rule: strip <c>[tags]</c>, convert <c>_</c> to <c>'</c>,
    /// collapse whitespace and title-case. Returns an empty string for null/blank input.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var result = TagRegex().Replace(name, " ");
        result = result.Replace('_', '\'');
        result = WhitespaceRegex().Replace(result, " ").Trim();
        return ToTitleCase(result);
    }

    /// <summary>
    /// Title-cases each whitespace-separated word. A single all-uppercase token is
    /// treated as an acronym/abbreviation and preserved (<c>SUACLPLNDRS</c>);
    /// multi-word names are title-cased even when typed in full caps. Characters
    /// after an apostrophe stay lowercase (<c>Mortal's</c>, never <c>Mortal'S</c>).
    /// </summary>
    public static string ToTitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var words = WhitespaceRegex().Replace(value.Trim(), " ").Split(' ');

        // Acronym preservation applies only to single-token names.
        if (words.Length == 1 && words[0].Length > 1 && words[0].All(char.IsUpper))
        {
            return words[0];
        }

        for (var i = 0; i < words.Length; i++)
        {
            var isEdge = i == 0 || i == words.Length - 1;
            words[i] = TitleCaseWord(words[i], isEdge);
        }

        return string.Join(' ', words);
    }

    private static string TitleCaseWord(string word, bool forceCapitalize)
    {
        if (word.Length == 0)
        {
            return word;
        }

        var lower = word.ToLowerInvariant();
        if (!forceCapitalize && SmallWords.Contains(lower))
        {
            return lower;
        }

        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}

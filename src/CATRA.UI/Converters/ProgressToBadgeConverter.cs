using System.Globalization;
using System.Windows.Data;
using CATRA.Core.Library;

namespace CATRA.UI.Converters;

/// <summary>
/// Converts an <see cref="EpisodeDetail"/> to its status badge glyph (Tela 2):
/// "✓" watched, "▶" has playback progress, "○" untouched.
/// (The "⚙ fila" badge belongs to the pre-processing window — ST-19.)
/// </summary>
[ValueConversion(typeof(EpisodeDetail), typeof(string))]
public sealed class ProgressToBadgeConverter : IValueConverter
{
    /// <summary>Badge for a watched episode.</summary>
    public const string WatchedGlyph = "✓";

    /// <summary>Badge for an episode with playback progress.</summary>
    public const string InProgressGlyph = "▶";

    /// <summary>Badge for an untouched episode.</summary>
    public const string UntouchedGlyph = "○";

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not EpisodeDetail detail)
        {
            return UntouchedGlyph;
        }

        if (detail.IsWatched)
        {
            return WatchedGlyph;
        }

        return detail.HasProgress ? InProgressGlyph : UntouchedGlyph;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("ProgressToBadgeConverter is one-way.");
}

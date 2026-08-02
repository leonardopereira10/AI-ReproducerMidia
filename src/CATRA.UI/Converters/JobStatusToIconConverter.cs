using System.Globalization;
using System.Windows.Data;
using CATRA.Core.Enums;

namespace CATRA.UI.Converters;

/// <summary>
/// Converts an <see cref="EpisodeProcessStatus"/> to its badge glyph (ST-19):
/// "⚙" fila (queued/processing), "✓" pronto (ready), "○" original, "↻" stale.
/// </summary>
[ValueConversion(typeof(EpisodeProcessStatus), typeof(string))]
public sealed class JobStatusToIconConverter : IValueConverter
{
    /// <summary>Glyph for an episode queued or being processed.</summary>
    public const string QueuedGlyph = "⚙";

    /// <summary>Glyph for a ready (processed) episode.</summary>
    public const string ReadyGlyph = "✓";

    /// <summary>Glyph for an untouched (original) episode.</summary>
    public const string OriginalGlyph = "○";

    /// <summary>Glyph for a stale episode (source changed).</summary>
    public const string StaleGlyph = "↻";

    /// <summary>Maps a status to its glyph (usable without a binding).</summary>
    public static string ToGlyph(EpisodeProcessStatus status) => status switch
    {
        EpisodeProcessStatus.Queued => QueuedGlyph,
        EpisodeProcessStatus.Processing => QueuedGlyph,
        EpisodeProcessStatus.Ready => ReadyGlyph,
        EpisodeProcessStatus.Stale => StaleGlyph,
        _ => OriginalGlyph,
    };

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is EpisodeProcessStatus s ? s : EpisodeProcessStatus.Original;
        return ToGlyph(status);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("JobStatusToIconConverter is one-way.");
}

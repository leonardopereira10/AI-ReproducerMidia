using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CATRA.Core.Enums;

namespace CATRA.UI.Converters;

/// <summary>
/// Converts an <see cref="EpisodeProcessStatus"/> to its badge foreground brush
/// (ST-19): ⚙ amarelo, ✓ verde, ○ cinza, ↻ laranja. Fixed semantic colors so
/// the badge meaning is stable across light/dark themes.
/// </summary>
[ValueConversion(typeof(EpisodeProcessStatus), typeof(Brush))]
public sealed class EpisodeProcessStatusToBrushConverter : IValueConverter
{
    /// <summary>Amarelo — queued/processing.</summary>
    public static readonly SolidColorBrush QueuedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xC2, 0x29)));

    /// <summary>Verde — ready.</summary>
    public static readonly SolidColorBrush ReadyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)));

    /// <summary>Cinza — original.</summary>
    public static readonly SolidColorBrush OriginalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));

    /// <summary>Laranja — stale.</summary>
    public static readonly SolidColorBrush StaleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x29)));

    /// <summary>Maps a status to its brush (usable without a binding).</summary>
    public static Brush ToBrush(EpisodeProcessStatus status) => status switch
    {
        EpisodeProcessStatus.Queued => QueuedBrush,
        EpisodeProcessStatus.Processing => QueuedBrush,
        EpisodeProcessStatus.Ready => ReadyBrush,
        EpisodeProcessStatus.Stale => StaleBrush,
        _ => OriginalBrush,
    };

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is EpisodeProcessStatus s ? s : EpisodeProcessStatus.Original;
        return ToBrush(status);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("EpisodeProcessStatusToBrushConverter is one-way.");

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

using System.Globalization;
using System.Windows.Data;

namespace CATRA.UI.Converters;

/// <summary>
/// Formats a playback timestamp as <c>"MM:SS"</c> (or <c>"H:MM:SS"</c> when the
/// value reaches one hour). Accepts <see cref="TimeSpan"/> or a numeric seconds
/// value; <c>null</c> / non-convertible inputs render as <c>"0:00"</c>. Used for
/// the player time display ("12:34 / 22:00") and the seek-bar drag tooltip.
/// </summary>
[ValueConversion(typeof(TimeSpan), typeof(string))]
public sealed class TimeSpanToStringConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        TimeSpan span = value switch
        {
            TimeSpan ts => ts,
            double d => TimeSpan.FromSeconds(double.IsNaN(d) ? 0d : d),
            float f => TimeSpan.FromSeconds(float.IsNaN(f) ? 0d : f),
            int i => TimeSpan.FromSeconds(i),
            long l => TimeSpan.FromSeconds(l),
            _ => TimeSpan.Zero,
        };

        return Format(span);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>Formats <paramref name="span"/> as <c>M:SS</c> or <c>H:MM:SS</c>.</summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1d
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }
}

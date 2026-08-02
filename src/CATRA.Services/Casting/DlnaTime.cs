using System.Globalization;

namespace CATRA.Services.Casting;

/// <summary>
/// UPnP AVTransport time formatting/parsing (<c>H:MM:SS</c> / <c>HH:MM:SS</c>).
/// Renderers return <c>NOT_IMPLEMENTED</c> for unknown values — mapped to
/// <see cref="TimeSpan.Zero"/>. Pure/static: fully unit-testable (ST-08).
/// </summary>
public static class DlnaTime
{
    /// <summary>Formats <paramref name="time"/> as <c>H:MM:SS</c> (clamped at zero).</summary>
    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
    }

    /// <summary>
    /// Parses <c>H:MM:SS</c> / <c>HH:MM:SS</c> (fractional seconds tolerated).
    /// Returns <see cref="TimeSpan.Zero"/> for null/empty/<c>NOT_IMPLEMENTED</c>/garbage.
    /// </summary>
    public static TimeSpan Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(':'))
        {
            var parts = trimmed.Split(':');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.Zero;
        }

        // Some renderers answer plain seconds.
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalSeconds)
            ? TimeSpan.FromSeconds(totalSeconds)
            : TimeSpan.Zero;
    }
}

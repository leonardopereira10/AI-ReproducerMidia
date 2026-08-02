using System.Globalization;
using System.Windows.Data;
using CATRA.Core.Enums;

namespace CATRA.UI.Converters;

/// <summary>
/// Maps a <see cref="PlaybackState"/> to the play/pause toggle glyph:
/// <c>"⏸"</c> while playing (the available action is pause), <c>"▶"</c>
/// otherwise (stopped / paused / seeking → the action is play).
/// </summary>
[ValueConversion(typeof(PlaybackState), typeof(string))]
public sealed class PlaybackStateToIconConverter : IValueConverter
{
    /// <summary>Glyph shown while playing (action: pause).</summary>
    public const string PauseIcon = "⏸";

    /// <summary>Glyph shown while not playing (action: play).</summary>
    public const string PlayIcon = "▶";

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PlaybackState.Playing ? PauseIcon : PlayIcon;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

using System.Globalization;
using System.Windows.Data;

namespace CATRA.UI.Converters;

/// <summary>
/// Maps a processing method key (stored in AppSettings) to its user-facing
/// label for the Settings "Processamento" combo boxes (ST-22):
/// rife → "RIFE v4", fsr3fg → "FSR 3 FG", fsr4 → "FSR 4", fsr1 → "FSR 1".
/// Unknown keys are shown verbatim.
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class ProcessMethodToLabelConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string key ? ToLabel(key) : string.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string ToLabel(string key) => key switch
    {
        "rife" => "RIFE v4",
        "fsr3fg" => "FSR 3 FG",
        "fsr4" => "FSR 4",
        "fsr1" => "FSR 1",
        _ => key
    };
}

using System.Globalization;
using System.Windows.Data;
using CATRA.Core.Enums;

namespace CATRA.UI.Converters;

/// <summary>
/// Maps an <see cref="AppTheme"/> to its user-facing label for the Settings
/// theme combo box (System → "Seguir Windows").
/// </summary>
[ValueConversion(typeof(AppTheme), typeof(string))]
public sealed class AppThemeToLabelConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AppTheme theme ? ToLabel(theme) : string.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string ToLabel(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "Seguir Windows"
    };
}

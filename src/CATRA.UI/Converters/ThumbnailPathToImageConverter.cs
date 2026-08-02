using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CATRA.UI.Converters;

/// <summary>
/// Converts a thumbnail file path (ST-09) into a <see cref="BitmapImage"/> for
/// an <c>Image.Source</c> binding. Returns <c>null</c> for a null/empty path,
/// a missing file or a decode failure so the underlying placeholder stays
/// visible (RF-08 fallback).
/// </summary>
[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class ThumbnailPathToImageConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // Corrupt/locked image — fall back to the placeholder.
            return null;
        }
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

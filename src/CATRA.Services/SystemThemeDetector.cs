using CATRA.Core.Enums;
using CATRA.Core.Interfaces;

namespace CATRA.Services;

/// <summary>
/// Detects the Windows system theme by reading the registry key
/// HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme.
/// </summary>
public sealed class SystemThemeDetector : ISystemThemeDetector
{
    private const string RegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string ValueName = "AppsUseLightTheme";

    /// <inheritdoc />
    public AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryPath);
            var value = key?.GetValue(ValueName);

            if (value is int intValue)
            {
                return intValue == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch
        {
            // Fall through to default.
        }

        // Default to light if the key is missing or unreadable.
        return AppTheme.Light;
    }
}

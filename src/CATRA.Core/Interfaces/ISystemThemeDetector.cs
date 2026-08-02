using CATRA.Core.Enums;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Detects the current Windows system theme (light or dark).
/// Abstracted for testability — the real implementation reads the registry.
/// </summary>
public interface ISystemThemeDetector
{
    /// <summary>
    /// Returns <see cref="AppTheme.Light"/> or <see cref="AppTheme.Dark"/>
    /// based on the current Windows personalization setting.
    /// Never returns <see cref="AppTheme.System"/>.
    /// </summary>
    AppTheme DetectSystemTheme();
}

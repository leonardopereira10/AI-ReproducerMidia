using CATRA.Core.Enums;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Manages application theme resolution (system vs. override) and notifies
/// subscribers when the effective theme changes.
/// </summary>
public interface IThemeService
{
    /// <summary>The currently resolved (effective) theme — always Light or Dark.</summary>
    AppTheme CurrentTheme { get; }

    /// <summary>The user's override preference (System, Light, or Dark).</summary>
    AppTheme Override { get; }

    /// <summary>Raised when the effective theme changes.</summary>
    event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>
    /// Sets the theme override and persists it.
    /// Pass <see cref="AppTheme.System"/> to follow Windows.
    /// </summary>
    void SetOverride(AppTheme theme);

    /// <summary>Starts polling for system theme changes.</summary>
    void Start();

    /// <summary>Stops polling.</summary>
    void Stop();
}

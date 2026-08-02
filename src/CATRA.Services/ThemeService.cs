using CATRA.Core.Enums;
using CATRA.Core.Interfaces;

namespace CATRA.Services;

/// <summary>
/// Resolves the effective application theme by combining the user override
/// preference with the Windows system theme. Polls for system changes every 5 seconds.
/// </summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    private const string OverrideSettingKey = "theme_override";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ISystemThemeDetector _systemThemeDetector;
    private readonly IAppSettingsRepository _settingsRepository;
    private readonly object _lock = new();

    private Timer? _pollTimer;
    private AppTheme _currentTheme;
    private AppTheme _override;
    private bool _disposed;

    public ThemeService(
        ISystemThemeDetector systemThemeDetector,
        IAppSettingsRepository settingsRepository)
    {
        _systemThemeDetector = systemThemeDetector ?? throw new ArgumentNullException(nameof(systemThemeDetector));
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));

        _override = LoadOverride();
        _currentTheme = ResolveTheme();
    }

    /// <inheritdoc />
    public AppTheme CurrentTheme
    {
        get { lock (_lock) { return _currentTheme; } }
    }

    /// <inheritdoc />
    public AppTheme Override
    {
        get { lock (_lock) { return _override; } }
    }

    /// <inheritdoc />
    public event EventHandler<AppTheme>? ThemeChanged;

    /// <inheritdoc />
    public void SetOverride(AppTheme theme)
    {
        AppTheme previous;
        AppTheme resolved;

        lock (_lock)
        {
            _override = theme;
            previous = _currentTheme;
            resolved = ResolveTheme();
            _currentTheme = resolved;
        }

        _settingsRepository.Set(OverrideSettingKey, ThemeToString(theme));

        if (previous != resolved)
        {
            ThemeChanged?.Invoke(this, resolved);
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lock)
        {
            if (_pollTimer is not null)
            {
                return;
            }

            _pollTimer = new Timer(PollCallback, null, PollInterval, PollInterval);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lock)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    /// <summary>
    /// Forces a re-evaluation of the effective theme. Used by polling and tests.
    /// </summary>
    public void Refresh()
    {
        AppTheme previous;
        AppTheme resolved;

        lock (_lock)
        {
            previous = _currentTheme;
            resolved = ResolveTheme();
            _currentTheme = resolved;
        }

        if (previous != resolved)
        {
            ThemeChanged?.Invoke(this, resolved);
        }
    }

    private void PollCallback(object? state)
    {
        if (_disposed)
        {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Resolves the effective theme: if override is Light or Dark, use it directly;
    /// otherwise detect the system theme.
    /// </summary>
    private AppTheme ResolveTheme()
    {
        if (_override == AppTheme.Light || _override == AppTheme.Dark)
        {
            return _override;
        }

        return _systemThemeDetector.DetectSystemTheme();
    }

    private AppTheme LoadOverride()
    {
        var raw = _settingsRepository.Get(OverrideSettingKey);
        return raw switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System
        };
    }

    private static string ThemeToString(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

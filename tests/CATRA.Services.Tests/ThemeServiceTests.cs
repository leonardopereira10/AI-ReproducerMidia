using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Services;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests;

/// <summary>
/// Unit tests for <see cref="ThemeService"/> decision logic (override vs. system).
/// Uses stubs for ISystemThemeDetector and IAppSettingsRepository — no WPF or registry dependency.
/// </summary>
public class ThemeServiceTests
{
    private readonly StubSystemThemeDetector _detector = new();
    private readonly StubAppSettingsRepository _settings = new();

    private ThemeService CreateService() => new(_detector, _settings);

    // --- Constructor / initial resolution ---

    [Fact]
    public void Ctor_NoOverride_SystemLight_ResolvesLight()
    {
        _detector.ThemeToReturn = AppTheme.Light;

        var service = CreateService();

        service.CurrentTheme.Should().Be(AppTheme.Light);
        service.Override.Should().Be(AppTheme.System);
    }

    [Fact]
    public void Ctor_NoOverride_SystemDark_ResolvesDark()
    {
        _detector.ThemeToReturn = AppTheme.Dark;

        var service = CreateService();

        service.CurrentTheme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void Ctor_OverrideLight_IgnoresSystem()
    {
        _settings.Store["theme_override"] = "light";
        _detector.ThemeToReturn = AppTheme.Dark; // system says dark, but override wins

        var service = CreateService();

        service.CurrentTheme.Should().Be(AppTheme.Light);
        service.Override.Should().Be(AppTheme.Light);
    }

    [Fact]
    public void Ctor_OverrideDark_IgnoresSystem()
    {
        _settings.Store["theme_override"] = "dark";
        _detector.ThemeToReturn = AppTheme.Light; // system says light, but override wins

        var service = CreateService();

        service.CurrentTheme.Should().Be(AppTheme.Dark);
        service.Override.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void Ctor_OverrideSystem_FollowsSystem()
    {
        _settings.Store["theme_override"] = "system";
        _detector.ThemeToReturn = AppTheme.Dark;

        var service = CreateService();

        service.CurrentTheme.Should().Be(AppTheme.Dark);
        service.Override.Should().Be(AppTheme.System);
    }

    // --- SetOverride ---

    [Fact]
    public void SetOverride_Light_PersistsAndResolves()
    {
        _detector.ThemeToReturn = AppTheme.Dark;
        var service = CreateService();

        service.SetOverride(AppTheme.Light);

        service.CurrentTheme.Should().Be(AppTheme.Light);
        service.Override.Should().Be(AppTheme.Light);
        _settings.Store["theme_override"].Should().Be("light");
    }

    [Fact]
    public void SetOverride_Dark_PersistsAndResolves()
    {
        _detector.ThemeToReturn = AppTheme.Light;
        var service = CreateService();

        service.SetOverride(AppTheme.Dark);

        service.CurrentTheme.Should().Be(AppTheme.Dark);
        _settings.Store["theme_override"].Should().Be("dark");
    }

    [Fact]
    public void SetOverride_System_FallsBackToSystemDetection()
    {
        _detector.ThemeToReturn = AppTheme.Dark;
        var service = CreateService();
        service.SetOverride(AppTheme.Light); // first override to light

        service.SetOverride(AppTheme.System); // back to system

        service.CurrentTheme.Should().Be(AppTheme.Dark);
        _settings.Store["theme_override"].Should().Be("system");
    }

    // --- ThemeChanged event ---

    [Fact]
    public void SetOverride_ThemeChanges_RaisesEvent()
    {
        _detector.ThemeToReturn = AppTheme.Light;
        var service = CreateService();
        AppTheme? received = null;
        service.ThemeChanged += (_, theme) => received = theme;

        service.SetOverride(AppTheme.Dark);

        received.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void SetOverride_SameTheme_DoesNotRaiseEvent()
    {
        _detector.ThemeToReturn = AppTheme.Light;
        var service = CreateService();
        var eventCount = 0;
        service.ThemeChanged += (_, _) => eventCount++;

        service.SetOverride(AppTheme.Light); // already light

        eventCount.Should().Be(0);
    }

    // --- Refresh (simulates polling) ---

    [Fact]
    public void Refresh_SystemThemeChanges_RaisesEvent()
    {
        _detector.ThemeToReturn = AppTheme.Light;
        var service = CreateService();
        AppTheme? received = null;
        service.ThemeChanged += (_, theme) => received = theme;

        _detector.ThemeToReturn = AppTheme.Dark;
        service.Refresh();

        received.Should().Be(AppTheme.Dark);
        service.CurrentTheme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void Refresh_OverrideSet_IgnoresSystemChange()
    {
        _detector.ThemeToReturn = AppTheme.Light;
        var service = CreateService();
        service.SetOverride(AppTheme.Light);

        var eventCount = 0;
        service.ThemeChanged += (_, _) => eventCount++;

        _detector.ThemeToReturn = AppTheme.Dark;
        service.Refresh();

        eventCount.Should().Be(0);
        service.CurrentTheme.Should().Be(AppTheme.Light);
    }

    [Fact]
    public void Refresh_NoChange_DoesNotRaiseEvent()
    {
        _detector.ThemeToReturn = AppTheme.Dark;
        var service = CreateService();
        var eventCount = 0;
        service.ThemeChanged += (_, _) => eventCount++;

        service.Refresh();

        eventCount.Should().Be(0);
    }

    // --- Stubs ---

    private sealed class StubSystemThemeDetector : ISystemThemeDetector
    {
        public AppTheme ThemeToReturn { get; set; } = AppTheme.Light;

        public AppTheme DetectSystemTheme() => ThemeToReturn;
    }

    private sealed class StubAppSettingsRepository : IAppSettingsRepository
    {
        public Dictionary<string, string> Store { get; } = new();

        public string? Get(string key) => Store.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value) => Store[key] = value;

        public IReadOnlyDictionary<string, string> GetAll() => Store;
    }
}

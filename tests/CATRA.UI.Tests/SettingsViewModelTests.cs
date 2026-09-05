using CATRA.Core.Enums;
using CATRA.Core.Models;
using CATRA.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CATRA.UI.Tests;

/// <summary>
/// Headless unit tests for <see cref="SettingsViewModel"/> (ST-11). No WPF
/// Window/Dispatcher and no Win32 dialogs: settings/theme/scanner/folder-picker
/// are faked. Covers loading, spec defaults, auto-save on change, validation
/// (skip intro / HTTP port / root folder), real-time theme override, root-folder
/// rescan and "Limpar agora" cache cleanup.
/// </summary>
public sealed class SettingsViewModelTests
{
    private sealed class Fixture
    {
        public FakeAppSettingsRepository Settings { get; }
        public FakeThemeService Theme { get; } = new();
        public FakeLibraryScanner Scanner { get; } = new();
        public FakeFolderPicker FolderPicker { get; } = new();
        public FakeNavigator Navigator { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeNativeBridge NativeBridge { get; } = new();
        public SettingsViewModel ViewModel { get; }

        public Fixture(IDictionary<string, string>? seed = null)
        {
            Settings = new FakeAppSettingsRepository(seed ?? new Dictionary<string, string>());
            ViewModel = new SettingsViewModel(
                Settings, Theme, Scanner, FolderPicker, Navigator, Dialogs, NativeBridge);
        }
    }

    private static string CreateTempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "catra-st11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // ------------------------------------------------------------------
    // Loading + defaults
    // ------------------------------------------------------------------

    [Fact]
    public void Ctor_loads_values_from_repository()
    {
        var fixture = new Fixture(new Dictionary<string, string>
        {
            [AppSettingsModel.RootFolderKey] = @"C:\Media",
            [AppSettingsModel.AutoScanKey] = "false",
            [AppSettingsModel.DefaultSkipIntroSecKey] = "120",
            [AppSettingsModel.ThemeOverrideKey] = "dark",
            [AppSettingsModel.DlnaHttpPortKey] = "8080",
            [AppSettingsModel.DlnaTransmitProcessedKey] = "false",
            [AppSettingsModel.ProcessedFolderKey] = @"C:\Proc",
            [AppSettingsModel.CleanupOnCloseKey] = "false",
            [AppSettingsModel.WindowSizeKey] = "7",
        });

        var vm = fixture.ViewModel;
        vm.RootFolder.Should().Be(@"C:\Media");
        vm.AutoScan.Should().BeFalse();
        vm.SkipIntroSec.Should().Be(120);
        vm.SelectedTheme.Should().Be(AppTheme.Dark);
        vm.DlnaHttpPort.Should().Be("8080");
        vm.DlnaTransmitProcessed.Should().BeFalse();
        vm.ProcessedFolder.Should().Be(@"C:\Proc");
        vm.CleanupOnClose.Should().BeFalse();
        vm.WindowSize.Should().Be(7);
    }

    [Fact]
    public void Ctor_applies_spec_defaults_when_keys_absent()
    {
        var vm = new Fixture().ViewModel;

        vm.RootFolder.Should().BeEmpty();
        vm.AutoScan.Should().BeTrue();
        vm.SkipIntroSec.Should().Be(85);
        vm.SkipIntroDisplay.Should().Be("1:25");
        vm.SelectedTheme.Should().Be(AppTheme.System);
        vm.DlnaHttpPort.Should().Be("auto");
        vm.DlnaTransmitProcessed.Should().BeTrue();
        vm.CleanupOnClose.Should().BeTrue();
        vm.WindowSize.Should().Be(5);
        vm.InterpMethod.Should().Be("rife");
        vm.UpscaleMethod.Should().Be("fsr1");
        vm.LocalFps.Should().Be(60);
        vm.DlnaFps.Should().Be(55);
    }

    [Fact]
    public void Ctor_does_not_write_back_to_repository()
    {
        var fixture = new Fixture(new Dictionary<string, string>
        {
            [AppSettingsModel.ThemeOverrideKey] = "dark",
        });

        // Hydration assigns backing fields directly; nothing is persisted on load.
        fixture.Settings.SetCalls.Should().BeEmpty();
        fixture.Theme.OverrideCalls.Should().BeEmpty();
        fixture.Scanner.ScanCalls.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Auto-save on change
    // ------------------------------------------------------------------

    [Fact]
    public void Changing_auto_scan_persists_immediately()
    {
        var fixture = new Fixture();
        fixture.ViewModel.AutoScan = false;

        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.AutoScanKey, "false"));
    }

    [Fact]
    public void Changing_cleanup_and_processed_folder_persist()
    {
        var fixture = new Fixture();
        fixture.ViewModel.CleanupOnClose = false;
        fixture.ViewModel.ProcessedFolder = @"C:\NewProc";

        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.CleanupOnCloseKey, "false"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.ProcessedFolderKey, @"C:\NewProc"));
    }

    // ------------------------------------------------------------------
    // Skip intro validation
    // ------------------------------------------------------------------

    [Fact]
    public void Valid_skip_intro_persists_and_formats_display()
    {
        var fixture = new Fixture();
        fixture.ViewModel.SkipIntroSec = 90;

        fixture.ViewModel.SkipIntroError.Should().BeEmpty();
        fixture.ViewModel.SkipIntroDisplay.Should().Be("1:30");
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.DefaultSkipIntroSecKey, "90"));
    }

    [Fact]
    public void Non_positive_skip_intro_is_rejected_and_not_persisted()
    {
        var fixture = new Fixture();
        fixture.ViewModel.SkipIntroSec = 0;

        fixture.ViewModel.SkipIntroError.Should().NotBeEmpty();
        fixture.Settings.SetCalls.Should().NotContain(c => c.Key == AppSettingsModel.DefaultSkipIntroSecKey);
    }

    // ------------------------------------------------------------------
    // HTTP port validation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("8080")]
    [InlineData("1")]
    [InlineData("65535")]
    public void Valid_port_persists(string port)
    {
        var fixture = new Fixture();
        fixture.ViewModel.DlnaHttpPort = port;

        fixture.ViewModel.PortError.Should().BeEmpty();
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.DlnaHttpPortKey, port));
    }

    [Fact]
    public void Switching_port_back_to_auto_persists()
    {
        var fixture = new Fixture();
        fixture.ViewModel.DlnaHttpPort = "8080";
        fixture.Settings.SetCalls.Clear();

        fixture.ViewModel.DlnaHttpPort = "auto";

        fixture.ViewModel.PortError.Should().BeEmpty();
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.DlnaHttpPortKey, "auto"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("")]
    public void Invalid_port_is_rejected_and_not_persisted(string port)
    {
        var fixture = new Fixture();
        fixture.ViewModel.DlnaHttpPort = port;

        fixture.ViewModel.PortError.Should().NotBeEmpty();
        fixture.Settings.SetCalls.Should().NotContain(c => c.Key == AppSettingsModel.DlnaHttpPortKey);
    }

    // ------------------------------------------------------------------
    // Theme real-time override
    // ------------------------------------------------------------------

    [Fact]
    public void Changing_theme_applies_override_in_real_time()
    {
        var fixture = new Fixture();
        fixture.ViewModel.SelectedTheme = AppTheme.Light;

        fixture.Theme.OverrideCalls.Should().ContainSingle().Which.Should().Be(AppTheme.Light);
        fixture.Theme.Override.Should().Be(AppTheme.Light);
    }

    // ------------------------------------------------------------------
    // Root folder: validation + rescan
    // ------------------------------------------------------------------

    [Fact]
    public void Valid_existing_root_folder_persists_and_triggers_scan()
    {
        var folder = CreateTempFolder();
        try
        {
            var fixture = new Fixture();
            fixture.ViewModel.RootFolder = folder;

            fixture.ViewModel.RootFolderError.Should().BeEmpty();
            fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.RootFolderKey, folder));
            fixture.Scanner.ScanCalls.Should().ContainSingle().Which.Should().Be(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Non_existent_root_folder_is_rejected_without_scan()
    {
        var fixture = new Fixture();
        fixture.ViewModel.RootFolder = Path.Combine(Path.GetTempPath(), "catra-does-not-exist-" + Guid.NewGuid().ToString("N"));

        fixture.ViewModel.RootFolderError.Should().NotBeEmpty();
        fixture.Settings.SetCalls.Should().NotContain(c => c.Key == AppSettingsModel.RootFolderKey);
        fixture.Scanner.ScanCalls.Should().BeEmpty();
    }

    [Fact]
    public void Browse_root_folder_uses_picker_and_persists()
    {
        var folder = CreateTempFolder();
        try
        {
            var fixture = new Fixture();
            fixture.FolderPicker.Result = folder;

            fixture.ViewModel.BrowseRootFolderCommand.Execute(null);

            fixture.FolderPicker.Titles.Should().ContainSingle();
            fixture.ViewModel.RootFolder.Should().Be(folder);
            fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.RootFolderKey, folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // "Limpar agora"
    // ------------------------------------------------------------------

    [Fact]
    public async Task Clean_now_deletes_cache_and_reports_freed_space()
    {
        var folder = CreateTempFolder();
        try
        {
            var file = Path.Combine(folder, "processed.mp4");
            await File.WriteAllBytesAsync(file, new byte[2048]);

            var fixture = new Fixture();
            fixture.ViewModel.ProcessedFolder = folder;
            fixture.ViewModel.UsageText.Should().NotBe("0 B");

            await fixture.ViewModel.CleanNowCommand.ExecuteAsync(null);

            File.Exists(file).Should().BeFalse();
            fixture.ViewModel.UsageText.Should().Be("0 B");
            fixture.ViewModel.StatusMessage.Should().Contain("Liberados");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Clean_now_with_missing_folder_reports_nothing_to_clean()
    {
        var fixture = new Fixture();
        fixture.ViewModel.ProcessedFolder = string.Empty;

        await fixture.ViewModel.CleanNowCommand.ExecuteAsync(null);

        fixture.ViewModel.StatusMessage.Should().Be("Nada para limpar.");
    }

    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    [Fact]
    public void Go_back_navigates_back()
    {
        var fixture = new Fixture();
        fixture.ViewModel.GoBackCommand.Execute(null);

        fixture.Navigator.BackCount.Should().Be(1);
    }
}

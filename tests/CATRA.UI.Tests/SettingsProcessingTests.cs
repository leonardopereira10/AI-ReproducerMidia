using CATRA.Core.Models;
using CATRA.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CATRA.UI.Tests;

/// <summary>
/// Headless unit tests for the Settings "Processamento" section (ST-22):
/// loading from the repository, immediate persistence on change, clamp
/// validation (window size / FPS / resolution / bitrate), per-episode size
/// estimate and the FSR 4 availability warning.
/// </summary>
public sealed class SettingsProcessingTests
{
    private sealed class Fixture
    {
        public FakeAppSettingsRepository Settings { get; }
        public SettingsViewModel ViewModel { get; }

        public Fixture(IDictionary<string, string>? seed = null)
        {
            Settings = new FakeAppSettingsRepository(seed ?? new Dictionary<string, string>());
            ViewModel = new SettingsViewModel(
                Settings,
                new FakeThemeService(),
                new FakeLibraryScanner(),
                new FakeFolderPicker(),
                new FakeNavigator(),
                new FakeDialogService());
        }
    }

    // ------------------------------------------------------------------
    // Loading + defaults
    // ------------------------------------------------------------------

    [Fact]
    public void Ctor_loads_processing_values_from_repository()
    {
        var fixture = new Fixture(new Dictionary<string, string>
        {
            [AppSettingsModel.WindowSizeKey] = "8",
            [AppSettingsModel.InterpMethodKey] = "fsr3fg",
            [AppSettingsModel.UpscaleMethodKey] = "fsr1",
            [AppSettingsModel.LocalTargetWidthKey] = "2560",
            [AppSettingsModel.LocalTargetHeightKey] = "1440",
            [AppSettingsModel.LocalTargetFpsKey] = "60",
            [AppSettingsModel.LocalEncodeBitrateKey] = "30000",
            [AppSettingsModel.DlnaTargetWidthKey] = "1920",
            [AppSettingsModel.DlnaTargetHeightKey] = "1080",
            [AppSettingsModel.DlnaTargetFpsKey] = "30",
            [AppSettingsModel.DlnaEncodeBitrateKey] = "15000",
        });

        var vm = fixture.ViewModel;
        vm.WindowSize.Should().Be(8);
        vm.InterpMethod.Should().Be("fsr3fg");
        vm.UpscaleMethod.Should().Be("fsr1");
        vm.LocalWidth.Should().Be(2560);
        vm.LocalHeight.Should().Be(1440);
        vm.LocalFps.Should().Be(60);
        vm.LocalBitrate.Should().Be(30000);
        vm.DlnaWidth.Should().Be(1920);
        vm.DlnaHeight.Should().Be(1080);
        vm.DlnaFps.Should().Be(30);
        vm.DlnaBitrate.Should().Be(15000);
    }

    [Fact]
    public void Ctor_applies_spec_defaults_when_keys_absent()
    {
        var vm = new Fixture().ViewModel;

        vm.WindowSize.Should().Be(5);
        vm.InterpMethod.Should().Be("rife");
        vm.UpscaleMethod.Should().Be("fsr4");
        vm.LocalWidth.Should().Be(1920);
        vm.LocalHeight.Should().Be(1080);
        vm.LocalFps.Should().Be(135);
        vm.LocalBitrate.Should().Be(20000);
        vm.DlnaWidth.Should().Be(3840);
        vm.DlnaHeight.Should().Be(2160);
        vm.DlnaFps.Should().Be(55);
        vm.DlnaBitrate.Should().Be(45000);
    }

    [Fact]
    public void Ctor_does_not_persist_processing_values_on_load()
    {
        var fixture = new Fixture(new Dictionary<string, string>
        {
            [AppSettingsModel.WindowSizeKey] = "8",
        });

        fixture.Settings.SetCalls.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Immediate persistence
    // ------------------------------------------------------------------

    [Fact]
    public void Changing_window_size_persists_immediately()
    {
        var fixture = new Fixture();
        fixture.ViewModel.WindowSize = 10;

        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.WindowSizeKey, "10"));
    }

    [Fact]
    public void Changing_methods_persist_immediately()
    {
        var fixture = new Fixture();
        fixture.ViewModel.InterpMethod = "fsr3fg";
        fixture.ViewModel.UpscaleMethod = "fsr1";

        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.InterpMethodKey, "fsr3fg"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.UpscaleMethodKey, "fsr1"));
    }

    [Fact]
    public void Changing_local_profile_persists_all_keys()
    {
        var fixture = new Fixture();
        fixture.ViewModel.LocalWidth = 2560;
        fixture.ViewModel.LocalHeight = 1440;
        fixture.ViewModel.LocalFps = 60;
        fixture.ViewModel.LocalBitrate = 30000;

        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.LocalTargetWidthKey, "2560"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.LocalTargetHeightKey, "1440"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.LocalTargetFpsKey, "60"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.LocalEncodeBitrateKey, "30000"));
    }

    [Fact]
    public void Changing_dlna_profile_persists_all_keys()
    {
        var fixture = new Fixture();
        fixture.ViewModel.DlnaWidth = 1920;
        fixture.ViewModel.DlnaHeight = 1080;
        fixture.ViewModel.DlnaFps = 30;
        fixture.ViewModel.DlnaBitrate = 15000;

        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.DlnaTargetWidthKey, "1920"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.DlnaTargetHeightKey, "1080"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.DlnaTargetFpsKey, "30"));
        fixture.Settings.SetCalls.Should().Contain((AppSettingsModel.DlnaEncodeBitrateKey, "15000"));
    }

    // ------------------------------------------------------------------
    // Clamp validation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(21, 20)]
    [InlineData(100, 20)]
    public void Window_size_is_clamped_and_persisted(int input, int expected)
    {
        var fixture = new Fixture();
        fixture.ViewModel.WindowSize = input;

        fixture.ViewModel.WindowSize.Should().Be(expected);
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.WindowSizeKey, expected.ToString()));
    }

    [Theory]
    [InlineData(10, 24)]
    [InlineData(500, 240)]
    public void Local_fps_is_clamped_and_persisted(int input, int expected)
    {
        var fixture = new Fixture();
        fixture.ViewModel.LocalFps = input;

        fixture.ViewModel.LocalFps.Should().Be(expected);
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.LocalTargetFpsKey, expected.ToString()));
    }

    [Theory]
    [InlineData(10, 24)]
    [InlineData(500, 240)]
    public void Dlna_fps_is_clamped_and_persisted(int input, int expected)
    {
        var fixture = new Fixture();
        fixture.ViewModel.DlnaFps = input;

        fixture.ViewModel.DlnaFps.Should().Be(expected);
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.DlnaTargetFpsKey, expected.ToString()));
    }

    [Theory]
    [InlineData(100, 640)]
    [InlineData(9000, 7680)]
    public void Width_is_clamped_and_persisted(int input, int expected)
    {
        var fixture = new Fixture();
        fixture.ViewModel.LocalWidth = input;

        fixture.ViewModel.LocalWidth.Should().Be(expected);
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.LocalTargetWidthKey, expected.ToString()));
    }

    [Theory]
    [InlineData(100, 360)]
    [InlineData(5000, 4320)]
    public void Height_is_clamped_and_persisted(int input, int expected)
    {
        var fixture = new Fixture();
        fixture.ViewModel.DlnaHeight = input;

        fixture.ViewModel.DlnaHeight.Should().Be(expected);
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.DlnaTargetHeightKey, expected.ToString()));
    }

    [Theory]
    [InlineData(500, 1000)]
    [InlineData(300000, 200000)]
    public void Bitrate_is_clamped_and_persisted(int input, int expected)
    {
        var fixture = new Fixture();
        fixture.ViewModel.LocalBitrate = input;

        fixture.ViewModel.LocalBitrate.Should().Be(expected);
        fixture.Settings.SetCalls.Should().ContainSingle()
            .Which.Should().Be((AppSettingsModel.LocalEncodeBitrateKey, expected.ToString()));
    }

    [Fact]
    public void In_range_values_are_not_modified()
    {
        var fixture = new Fixture();
        fixture.ViewModel.LocalFps = 120;

        fixture.ViewModel.LocalFps.Should().Be(120);
    }

    // ------------------------------------------------------------------
    // Size estimate
    // ------------------------------------------------------------------

    [Fact]
    public void Default_estimates_match_spec_examples()
    {
        var vm = new Fixture().ViewModel;

        vm.LocalSizeEstimate.Should().Be("~3.3 GB por episódio (22min)");
        vm.DlnaSizeEstimate.Should().Be("~7.4 GB por episódio (22min)");
    }

    [Fact]
    public void Estimate_updates_when_bitrate_changes()
    {
        var fixture = new Fixture();
        fixture.ViewModel.LocalBitrate = 40000;

        fixture.ViewModel.LocalSizeEstimate.Should().Be("~6.6 GB por episódio (22min)");
    }

    [Fact]
    public void Estimate_scales_linearly_with_bitrate()
    {
        // Doubling the bitrate doubles the estimate (22min × bitrate / 8).
        var fixture = new Fixture();
        fixture.ViewModel.LocalBitrate = 10000;
        var half = fixture.ViewModel.LocalSizeEstimate;

        fixture.ViewModel.LocalBitrate = 20000;
        half.Should().Be("~1.7 GB por episódio (22min)");
        fixture.ViewModel.LocalSizeEstimate.Should().Be("~3.3 GB por episódio (22min)");
    }

    // ------------------------------------------------------------------
    // FSR 4 availability warning
    // ------------------------------------------------------------------

    [Fact]
    public void No_warning_when_fsr4_available()
    {
        var vm = new Fixture().ViewModel;

        vm.UpscaleMethod.Should().Be("fsr4");
        vm.UpscaleWarning.Should().BeEmpty();
    }

    [Fact]
    public void Warning_shown_when_fsr4_selected_but_unavailable()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Fsr4Available = false;

        fixture.ViewModel.UpscaleWarning.Should().Contain("FSR 4").And.Contain("FSR 1");
    }

    [Fact]
    public void Warning_clears_when_switching_to_fsr1()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Fsr4Available = false;
        fixture.ViewModel.UpscaleWarning.Should().NotBeEmpty();

        fixture.ViewModel.UpscaleMethod = "fsr1";

        fixture.ViewModel.UpscaleWarning.Should().BeEmpty();
    }

    [Fact]
    public void Warning_returns_when_switching_back_to_fsr4()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Fsr4Available = false;
        fixture.ViewModel.UpscaleMethod = "fsr1";

        fixture.ViewModel.UpscaleMethod = "fsr4";

        fixture.ViewModel.UpscaleWarning.Should().NotBeEmpty();
    }
}

using CATRA.Core.Enums;
using CATRA.Core.Models;
using CATRA.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace CATRA.UI.Tests;

/// <summary>
/// Headless unit tests for <see cref="PlayerViewModel"/> (ST-06). No WPF
/// Window/Dispatcher is created: the VM marshals engine events through
/// <see cref="System.Threading.SynchronizationContext"/>, which is <c>null</c>
/// here and therefore executes inline. Covers RN-04 (skip intro), RN-03
/// (resume dialog), transport commands, volume and position updates.
/// </summary>
public sealed class PlayerViewModelTests
{
    private const int EpisodeId = 7;
    private const int MediaItemId = 3;

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(22); // 1320s

    private sealed class Fixture
    {
        public FakePlaybackEngine Engine { get; } = new();
        public FakeEpisodeRepository Episodes { get; } = new();
        public FakeMediaItemRepository MediaItems { get; } = new();
        public FakeWatchStateRepository WatchStates { get; } = new();
        public FakeWatchStateService WatchStateService { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeNavigator Navigator { get; } = new();
        public FakeCastingService Casting { get; } = new();
        public PlayerViewModel ViewModel { get; }

        public Fixture(double skipIntroSec = 85.0)
        {
            // xUnit installs a SynchronizationContext; the VM marshals engine
            // events through it. Clearing it makes RunOnUi execute inline so
            // assertions observe the updated bindables synchronously.
            SynchronizationContext.SetSynchronizationContext(null);

            MediaItems.Add(new MediaItem
            {
                Id = MediaItemId,
                Title = "Serie",
                SkipIntroSec = skipIntroSec,
            });
            Episodes.Add(new Episode
            {
                Id = EpisodeId,
                MediaItemId = MediaItemId,
                FileName = "s01e01.mkv",
                FilePath = @"C:\media\s01e01.mkv",
                DisplayTitle = "Piloto",
            });

            ViewModel = new PlayerViewModel(
                Engine, Episodes, MediaItems, WatchStates, WatchStateService, Dialogs, Navigator, Casting);
        }

        public void SetWatchState(double progressPct, double lastPositionSec, bool watched = false)
            => WatchStates.Add(new WatchState
            {
                Id = 1,
                EpisodeId = EpisodeId,
                ProgressPct = progressPct,
                LastPositionSec = lastPositionSec,
                Watched = watched,
            });
    }

    // ------------------------------------------------------------------
    // RN-04 — Pular Abertura
    // ------------------------------------------------------------------

    [Fact]
    public async Task SkipIntro_SeeksCurrentPlusSkipIntroSec()
    {
        var f = new Fixture(skipIntroSec: 85.0);
        await f.ViewModel.OpenAsync(EpisodeId);
        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(100));

        f.ViewModel.SkipIntroCommand.Execute(null);

        f.Engine.SeekCalls.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromSeconds(185));
        f.ViewModel.Position.Should().Be(TimeSpan.FromSeconds(185));
        f.ViewModel.PositionSeconds.Should().Be(185);
    }

    [Fact]
    public async Task SkipIntro_UsesMediaItemSkipIntroSecOverride()
    {
        var f = new Fixture(skipIntroSec: 120.0);
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.SkipIntroSec.Should().Be(120.0);
        f.ViewModel.SkipIntroTooltip.Should().Be("Pular +2:00");
    }

    [Fact]
    public async Task SkipIntro_DefaultsTo85SecondsWhenNoOverride()
    {
        var f = new Fixture();
        f.MediaItems.GetAll().First().SkipIntroSec = 85.0;
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.SkipIntroSec.Should().Be(PlayerViewModel.DefaultSkipIntroSec);
        f.ViewModel.SkipIntroTooltip.Should().Be("Pular +1:25");
    }

    [Fact]
    public async Task SkipIntro_DisabledNearEnd()
    {
        var f = new Fixture(skipIntroSec: 85.0);
        await f.ViewModel.OpenAsync(EpisodeId);
        // duration 1320s; guard 30s -> disabled when pos+85 > 1290 -> pos > 1205.
        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(1250));

        // RN-04: the button is disabled near the end. (CommunityToolkit.Mvvm
        // RelayCommand.Execute does not gate on CanExecute; the disabled-button
        // guard is enforced by the WPF binding, so we assert CanExecute here.)
        f.ViewModel.SkipIntroCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task SkipIntro_EnabledAtStart()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.SkipIntroCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1320, 85, true)]     // start: 85 <= 1290
    [InlineData(1205, 1320, 85, true)]  // boundary: 1290 <= 1290
    [InlineData(1206, 1320, 85, false)] // just past boundary
    [InlineData(1250, 1320, 85, false)] // near end
    [InlineData(0, 0, 85, false)]       // no duration
    [InlineData(0, 1320, 0, false)]     // no skip
    public void CanSkipIntroAt_BoundaryMatrix(
        double posSec, double durSec, double skip, bool expected)
    {
        PlayerViewModel.CanSkipIntroAt(
            TimeSpan.FromSeconds(posSec),
            TimeSpan.FromSeconds(durSec),
            skip).Should().Be(expected);
    }

    // ------------------------------------------------------------------
    // RN-03 — Continuar assistindo
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(50, 120, true)]   // in progress -> offer
    [InlineData(84.9, 31, true)]  // just under both thresholds -> offer
    [InlineData(85, 120, false)]  // at/over progress threshold -> no
    [InlineData(90, 120, false)]  // watched far -> no
    [InlineData(50, 30, false)]   // at/under position threshold -> no
    [InlineData(50, 10, false)]   // too early -> no
    public void ShouldOfferResume_Matrix(double pct, double pos, bool expected)
        => PlayerViewModel.ShouldOfferResume(pct, pos).Should().Be(expected);

    [Fact]
    public async Task Open_OffersResume_AndContinuesAtLastPosition()
    {
        var f = new Fixture();
        f.SetWatchState(progressPct: 50, lastPositionSec: 120);
        f.Dialogs.ConfirmResults.Enqueue(true); // "Continuar"

        await f.ViewModel.OpenAsync(EpisodeId);

        f.Dialogs.ConfirmCalls.Should().HaveCount(1);
        f.Dialogs.ConfirmCalls[0].Accept.Should().Be("Continuar");
        f.Dialogs.ConfirmCalls[0].Cancel.Should().Be("Do início");
        f.Engine.SeekCalls.Should().Contain(TimeSpan.FromSeconds(120));
        f.ViewModel.Position.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task Open_ResumeDeclined_StartsFromBeginning()
    {
        var f = new Fixture();
        f.SetWatchState(progressPct: 50, lastPositionSec: 120);
        f.Dialogs.ConfirmResults.Enqueue(false); // "Do início"

        await f.ViewModel.OpenAsync(EpisodeId);

        f.Dialogs.ConfirmCalls.Should().HaveCount(1);
        f.Engine.SeekCalls.Should().NotContain(TimeSpan.FromSeconds(120));
        f.ViewModel.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Open_NoResume_WhenProgressOverThreshold()
    {
        var f = new Fixture();
        f.SetWatchState(progressPct: 90, lastPositionSec: 500);

        await f.ViewModel.OpenAsync(EpisodeId);

        f.Dialogs.ConfirmCalls.Should().BeEmpty();
        f.ViewModel.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Open_NoResume_WhenNoWatchState()
    {
        var f = new Fixture();

        await f.ViewModel.OpenAsync(EpisodeId);

        f.Dialogs.ConfirmCalls.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Transport commands
    // ------------------------------------------------------------------

    [Fact]
    public async Task Open_StartsPlayback()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.Engine.PlayCount.Should().Be(1);
        f.ViewModel.State.Should().Be(PlaybackState.Playing);
        f.ViewModel.Duration.Should().Be(Duration);
        f.Engine.OpenedPaths.Should().ContainSingle()
            .Which.Should().Be(@"C:\media\s01e01.mkv");
    }

    [Fact]
    public async Task Pause_CallsEngineAndUpdatesState()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.PauseCommand.Execute(null);

        f.Engine.PauseCount.Should().Be(1);
        f.ViewModel.State.Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public async Task Stop_ResetsPositionAndState()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(300));

        f.ViewModel.StopCommand.Execute(null);

        f.Engine.StopCount.Should().Be(1);
        f.ViewModel.State.Should().Be(PlaybackState.Stopped);
        f.ViewModel.Position.Should().Be(TimeSpan.Zero);
        f.ViewModel.PositionSeconds.Should().Be(0);
    }

    [Fact]
    public async Task PlayPauseToggle_TogglesBetweenPlayAndPause()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId); // Playing

        await f.ViewModel.PlayPauseToggleCommand.ExecuteAsync(null);
        f.ViewModel.State.Should().Be(PlaybackState.Paused);

        await f.ViewModel.PlayPauseToggleCommand.ExecuteAsync(null);
        f.ViewModel.State.Should().Be(PlaybackState.Playing);
    }

    [Fact]
    public async Task Play_AfterStop_ReopensMedia()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.ViewModel.StopCommand.Execute(null);
        f.Engine.OpenedPaths.Clear();

        await f.ViewModel.PlayCommand.ExecuteAsync(null);

        f.Engine.OpenedPaths.Should().HaveCount(1);
        f.ViewModel.State.Should().Be(PlaybackState.Playing);
    }

    [Fact]
    public async Task Seek_ClampsToDuration()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.SeekCommand.Execute(Duration.TotalSeconds + 500);

        f.Engine.SeekCalls.Should().ContainSingle()
            .Which.Should().Be(Duration);
        f.ViewModel.PositionSeconds.Should().Be(Duration.TotalSeconds);
    }

    [Fact]
    public async Task Seek_NegativeClampsToZero()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.SeekCommand.Execute(-10d);

        f.Engine.SeekCalls.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.Zero);
    }

    // ------------------------------------------------------------------
    // Volume
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetVolume_PushesNormalizedValueToEngine()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.Volume = 50d;

        f.Engine.VolumeCalls.Should().Contain(0.5f);
    }

    [Fact]
    public async Task ToggleMute_ForcesZeroAndRestores()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.ViewModel.Volume = 80d;

        f.ViewModel.ToggleMuteCommand.Execute(null);
        f.ViewModel.IsMuted.Should().BeTrue();
        f.Engine.VolumeCalls.Should().Contain(0f);

        f.ViewModel.ToggleMuteCommand.Execute(null);
        f.ViewModel.IsMuted.Should().BeFalse();
        f.Engine.VolumeCalls.Should().Contain(0.8f);
    }

    // ------------------------------------------------------------------
    // Engine event -> bindable propagation
    // ------------------------------------------------------------------

    [Fact]
    public async Task PositionChanged_UpdatesPositionBindables()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(754));

        f.ViewModel.Position.Should().Be(TimeSpan.FromSeconds(754));
        f.ViewModel.PositionSeconds.Should().Be(754);
    }

    [Fact]
    public async Task PositionChanged_SuppressedWhileSeeking()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.ViewModel.BeginSeekCommand.Execute(null);
        f.ViewModel.PositionSeconds = 999;

        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(10));

        f.ViewModel.PositionSeconds.Should().Be(999); // preview preserved
        f.ViewModel.IsSeeking.Should().BeTrue();
    }

    [Fact]
    public async Task EndSeek_CommitsPreviewToEngine()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.ViewModel.BeginSeekCommand.Execute(null);
        f.ViewModel.PositionSeconds = 500;

        f.ViewModel.EndSeekCommand.Execute(null);

        f.ViewModel.IsSeeking.Should().BeFalse();
        f.Engine.SeekCalls.Should().Contain(TimeSpan.FromSeconds(500));
    }

    [Fact]
    public async Task StateChanged_UpdatesStateBindable()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.Engine.Pause(); // raises StateChanged

        f.ViewModel.State.Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public async Task MediaEnded_SetsStoppedAndPositionToDuration()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.Engine.RaiseMediaEnded();

        f.ViewModel.State.Should().Be(PlaybackState.Stopped);
        f.ViewModel.Position.Should().Be(Duration);
    }

    [Fact]
    public async Task EngineError_SetsErrorMessage()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.Engine.RaiseError("boom");

        f.ViewModel.HasError.Should().BeTrue();
        f.ViewModel.ErrorMessage.Should().Be("boom");
    }

    // ------------------------------------------------------------------
    // Misc lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public async Task Open_UnknownEpisode_SetsError()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(999);

        f.ViewModel.HasError.Should().BeTrue();
        f.Engine.OpenedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_StopsAndNavigatesBack_Once()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.CloseCommand.Execute(null);
        f.ViewModel.CloseCommand.Execute(null); // idempotent

        f.Navigator.BackCount.Should().Be(1);
        f.Engine.StopCount.Should().Be(1);
    }

    [Fact]
    public async Task ToggleFullscreen_FlipsFlag()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.ViewModel.ToggleFullscreenCommand.Execute(null);
        f.ViewModel.IsFullscreen.Should().BeTrue();

        f.ViewModel.ToggleFullscreenCommand.Execute(null);
        f.ViewModel.IsFullscreen.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // RF-05 / RN-08 — progress persistence (final save)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stop_PersistsFinalProgress()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(300));

        f.ViewModel.StopCommand.Execute(null);

        f.WatchStateService.SaveProgressCalls.Should().ContainSingle()
            .Which.Should().Be((EpisodeId, 300d, Duration.TotalSeconds));
    }

    [Fact]
    public async Task Close_PersistsFinalProgress()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);
        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(660));

        f.ViewModel.CloseCommand.Execute(null);

        f.WatchStateService.SaveProgressCalls.Should().ContainSingle()
            .Which.Should().Be((EpisodeId, 660d, Duration.TotalSeconds));
    }

    [Fact]
    public async Task MediaEnded_PersistsProgressAtDuration()
    {
        var f = new Fixture();
        await f.ViewModel.OpenAsync(EpisodeId);

        f.Engine.RaiseMediaEnded();

        // Natural end saves position == duration, which the service maps to watched.
        f.WatchStateService.SaveProgressCalls.Should().ContainSingle()
            .Which.Should().Be((EpisodeId, Duration.TotalSeconds, Duration.TotalSeconds));
    }

    [Fact]
    public void Dispose_DetachesSingletonEngineEvents_AndIsIdempotent()
    {
        var f = new Fixture();
        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(42));
        f.ViewModel.PositionSeconds.Should().Be(42);

        // ST-19 follow-up: the navigation service disposes transient VMs when
        // their page is left; PlayerViewModel.Dispose must detach the singleton
        // engine events (leak fix) and be safe to call more than once.
        f.ViewModel.Dispose();
        f.ViewModel.Dispose();

        f.Engine.RaisePositionChanged(TimeSpan.FromSeconds(99));
        f.ViewModel.PositionSeconds.Should().Be(42,
            "Dispose must unsubscribe from the singleton engine so no further updates arrive");
    }
}

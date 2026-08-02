using CATRA.Services.Playback;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Playback;

/// <summary>
/// Unit tests for the monotonic A/V sync <see cref="Clock"/> (ST-05): anchoring,
/// running/paused behaviour, seek re-anchor and reset.
/// </summary>
public class ClockTests
{
    [Fact]
    public void NewClock_StartsAtZero_AndIsNotRunning()
    {
        var clock = new Clock();

        clock.Current.Should().Be(TimeSpan.Zero);
        clock.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Start_MakesClockAdvance()
    {
        var clock = new Clock();
        clock.Start();

        clock.IsRunning.Should().BeTrue();
        // Give the stopwatch a moment to accumulate.
        SpinWait.SpinUntil(() => clock.Current > TimeSpan.Zero, TimeSpan.FromSeconds(2));
        clock.Current.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Set_ReanchorsPosition_WithoutStarting()
    {
        var clock = new Clock();

        clock.Set(TimeSpan.FromSeconds(5));

        clock.Current.Should().BeCloseTo(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
        clock.IsRunning.Should().BeFalse("Set preserves the stopped state of a never-started clock");
    }

    [Fact]
    public void Set_WhileRunning_KeepsAdvancingFromNewAnchor()
    {
        var clock = new Clock();
        clock.Start();

        clock.Set(TimeSpan.FromSeconds(1));
        SpinWait.SpinUntil(() => clock.Current > TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        clock.Current.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Pause_FreezesPosition()
    {
        var clock = new Clock();
        clock.Start();
        clock.Set(TimeSpan.FromSeconds(2));

        clock.Pause();
        TimeSpan frozen = clock.Current;

        clock.IsRunning.Should().BeFalse();
        Thread.Sleep(30);
        clock.Current.Should().BeCloseTo(frozen, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void Pause_ThenStart_ResumesFromFrozenPosition()
    {
        var clock = new Clock();
        clock.Start();
        clock.Set(TimeSpan.FromSeconds(3));
        clock.Pause();
        TimeSpan frozen = clock.Current;

        clock.Start();
        SpinWait.SpinUntil(() => clock.Current > frozen, TimeSpan.FromSeconds(2));

        clock.Current.Should().BeGreaterThan(frozen);
    }

    [Fact]
    public void Reset_RewindsToZero_AndStops()
    {
        var clock = new Clock();
        clock.Start();
        clock.Set(TimeSpan.FromSeconds(9));

        clock.Reset();

        clock.Current.Should().Be(TimeSpan.Zero);
        clock.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Set_AfterPause_DoesNotResurrectRunningState()
    {
        var clock = new Clock();
        clock.Start();
        clock.Pause();

        clock.Set(TimeSpan.FromSeconds(4));

        clock.IsRunning.Should().BeFalse("the clock was paused before the seek re-anchor");
        clock.Current.Should().BeCloseTo(TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(50));
    }
}

using CATRA.Core.Enums;
using CATRA.Services.Playback;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Playback;

/// <summary>
/// Lifecycle state-machine tests for <see cref="PlaybackEngine"/> (ST-05):
/// Stopped → Playing ↔ Paused → Stopped and the <c>StateChanged</c> sequence.
/// Uses an endless audio fake so the engine stays in Playing until told otherwise.
/// </summary>
public class StateMachineTests
{
    [Fact]
    public async Task FullLifecycle_RaisesExpectedStateSequence()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        var transitions = new List<PlaybackState>();
        h.Engine.StateChanged += (_, s) =>
        {
            lock (transitions) { transitions.Add(s); }
        };

        h.Engine.State.Should().Be(PlaybackState.Stopped);

        h.Engine.Play();
        h.Engine.State.Should().Be(PlaybackState.Playing);

        h.Engine.Pause();
        h.Engine.State.Should().Be(PlaybackState.Paused);

        h.Engine.Play();
        h.Engine.State.Should().Be(PlaybackState.Playing);

        h.Engine.Stop();
        h.Engine.State.Should().Be(PlaybackState.Stopped);

        lock (transitions)
        {
            transitions.Should().Equal(
                PlaybackState.Playing,
                PlaybackState.Paused,
                PlaybackState.Playing,
                PlaybackState.Stopped);
        }
    }

    [Fact]
    public async Task Play_WhenAlreadyPlaying_IsNoOp()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        int changes = 0;
        h.Engine.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        h.Engine.Play();
        h.Engine.Play();

        h.AudioRenderer.PlayCount.Should().Be(1, "the second Play is a no-op while already playing");
        Volatile.Read(ref changes).Should().Be(1);
        h.Engine.Stop();
    }

    [Fact]
    public async Task Pause_WhenNotPlaying_IsNoOp()
    {
        using var h = TestEngines.Create();
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Pause();

        h.Engine.State.Should().Be(PlaybackState.Stopped);
        h.AudioRenderer.PauseCount.Should().Be(0);
    }

    [Fact]
    public async Task Stop_AudioRendererStopped_AndResourcesReleased()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Play();
        h.Engine.Stop();

        h.AudioRenderer.StopCount.Should().BeGreaterThanOrEqualTo(1);
        h.Engine.State.Should().Be(PlaybackState.Stopped);
        h.Engine.Metadata.Should().BeNull("per-media resources are released on stop");
    }

    [Fact]
    public async Task Seek_WhenStopped_IsIgnored()
    {
        using var h = TestEngines.Create();
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Seek(TimeSpan.FromSeconds(5));

        h.Engine.State.Should().Be(PlaybackState.Stopped);
        h.VideoDecoder.SeekCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Seek_Negative_Throws()
    {
        using var h = TestEngines.Create();
        await h.Engine.OpenAsync("movie.mp4");

        Action act = () => h.Engine.Seek(TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Open_WhilePlaying_Throws()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");
        h.Engine.Play();

        Func<Task> act = () => h.Engine.OpenAsync("other.mp4");

        await act.Should().ThrowAsync<InvalidOperationException>();
        h.Engine.Stop();
    }
}

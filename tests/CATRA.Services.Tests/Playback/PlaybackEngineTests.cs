using CATRA.Core.Enums;
using CATRA.Core.Models;
using CATRA.Services.Playback;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Playback;

/// <summary>
/// Builds a <see cref="PlaybackEngine"/> wired to in-memory fakes (no FFmpeg, GPU
/// or audio device) so the orchestration logic is fully unit-testable.
/// </summary>
internal static class TestEngines
{
    public sealed class Harness : IDisposable
    {
        public required PlaybackEngine Engine { get; init; }
        public required FakeVideoDecoder VideoDecoder { get; init; }
        public required FakeAudioDecoder AudioDecoder { get; init; }
        public required FakeVideoRenderer VideoRenderer { get; init; }
        public required FakeAudioRenderer AudioRenderer { get; init; }

        public void Dispose() => Engine.Dispose();
    }

    public static Harness Create(TimeSpan? positionInterval = null)
    {
        var videoDecoder = new FakeVideoDecoder();
        var audioDecoder = new FakeAudioDecoder();
        var videoRenderer = new FakeVideoRenderer();
        var audioRenderer = new FakeAudioRenderer();

        var engine = new PlaybackEngine(
            () => videoDecoder,
            () => audioDecoder,
            () => videoRenderer,
            () => audioRenderer,
            clock: new Clock(),
            positionReportInterval: positionInterval ?? TimeSpan.FromMilliseconds(15));

        return new Harness
        {
            Engine = engine,
            VideoDecoder = videoDecoder,
            AudioDecoder = audioDecoder,
            VideoRenderer = videoRenderer,
            AudioRenderer = audioRenderer,
        };
    }
}

/// <summary>
/// Orchestration tests for <see cref="PlaybackEngine"/> (ST-05): open/play/end,
/// error propagation, position reporting, seek flushing and volume clamping.
/// All collaborators are fakes — no native playback occurs.
/// </summary>
public class PlaybackEngineTests
{
    [Fact]
    public async Task OpenAsync_ReturnsMetadata_AndExposesAudioTracks()
    {
        using var h = TestEngines.Create();
        h.VideoDecoder.AddTrack(new AudioTrack(1, "eng", "aac", 2, 48000));
        h.VideoDecoder.AddTrack(new AudioTrack(2, "jpn", "opus", 6, 48000));

        VideoMetadata metadata = await h.Engine.OpenAsync("movie.mkv");

        metadata.Title.Should().Be("fake-title");
        h.Engine.Metadata.Should().NotBeNull();
        h.Engine.AudioTracks.Should().HaveCount(2);
        h.Engine.State.Should().Be(PlaybackState.Stopped, "opening does not start playback");
        h.VideoDecoder.OpenedPath.Should().Be("movie.mkv");
        h.AudioDecoder.Opened.Should().BeTrue();
        h.AudioRenderer.Initialized.Should().BeTrue();
    }

    [Fact]
    public async Task OpenAsync_InitializesAudioRendererWithDecoderFormat()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.SampleRate = 44100;
        h.AudioDecoder.Channels = 6;

        await h.Engine.OpenAsync("movie.mkv");

        h.AudioRenderer.Volume.Should().Be(1f);
        // Position derives from the negotiated rate; nothing consumed yet.
        h.AudioRenderer.Position.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Play_WithoutOpen_Throws()
    {
        using var h = TestEngines.Create();

        Action act = () => h.Engine.Play();

        act.Should().Throw<InvalidOperationException>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Play_PresentsAllFrames_ThenRaisesMediaEnded()
    {
        using var h = TestEngines.Create();
        h.VideoDecoder.AddFrames(5);
        h.AudioDecoder.RemainingBuffers = 0; // audio exhausted immediately -> video ungated
        await h.Engine.OpenAsync("movie.mp4");

        using var ended = new ManualResetEventSlim(false);
        h.Engine.MediaEnded += (_, _) => ended.Set();

        h.Engine.SetOutputWindow(new IntPtr(0x1));
        h.Engine.Play();

        ended.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the engine should reach end of stream");
        SpinWait.SpinUntil(() => h.Engine.State == PlaybackState.Stopped, TimeSpan.FromSeconds(2));

        h.VideoRenderer.PresentCount.Should().Be(5);
        h.Engine.State.Should().Be(PlaybackState.Stopped);
    }

    [Fact]
    public async Task DecoderFailure_RaisesErrorEvent()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.RemainingBuffers = 0;
        h.VideoDecoder.ThrowOnRead = new InvalidOperationException("decode boom");
        await h.Engine.OpenAsync("movie.mp4");

        using var errored = new ManualResetEventSlim(false);
        PlaybackErrorEventArgs? captured = null;
        h.Engine.Error += (_, args) =>
        {
            captured = args;
            errored.Set();
        };

        h.Engine.Play();

        errored.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("a decoder exception must surface as Error");
        captured.Should().NotBeNull();
        captured!.Exception.Should().BeOfType<InvalidOperationException>();
        SpinWait.SpinUntil(() => h.Engine.State == PlaybackState.Stopped, TimeSpan.FromSeconds(2));
        h.Engine.State.Should().Be(PlaybackState.Stopped);
    }

    [Fact]
    public async Task PositionChanged_FiresPeriodically_WhilePlaying()
    {
        using var h = TestEngines.Create(positionInterval: TimeSpan.FromMilliseconds(10));
        h.AudioDecoder.Infinite = true; // hold the engine in Playing
        await h.Engine.OpenAsync("movie.mp4");

        var positions = new List<TimeSpan>();
        h.Engine.PositionChanged += (_, p) =>
        {
            lock (positions) { positions.Add(p); }
        };

        h.Engine.Play();
        SpinWait.SpinUntil(() =>
        {
            lock (positions) { return positions.Count >= 2; }
        }, TimeSpan.FromSeconds(5)).Should().BeTrue("position should be reported repeatedly");

        h.Engine.Stop();

        lock (positions)
        {
            positions.Should().HaveCountGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public async Task Seek_FlushesDecodersAndAudio_AndRestoresState()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Play();
        h.Engine.Seek(TimeSpan.FromSeconds(2));

        h.VideoDecoder.SeekCalls.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
        h.AudioDecoder.SeekCalls.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
        h.AudioRenderer.FlushCount.Should().BeGreaterThanOrEqualTo(1);
        h.Engine.State.Should().Be(PlaybackState.Playing, "seeking resumes the prior playing state");
        // The master clock is re-anchored to the seek target; the fake audio runs
        // faster than real time so the position only ever grows past the target.
        h.Engine.GetPosition().Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));

        h.Engine.Stop();
    }

    [Fact]
    public async Task Seek_ClampsToDuration()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        h.VideoDecoder.AddFrames(1, duration: TimeSpan.FromSeconds(10));
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Play();
        h.Engine.Seek(TimeSpan.FromSeconds(999));

        h.VideoDecoder.SeekCalls.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(10));
        h.Engine.Stop();
    }

    [Fact]
    public async Task Seek_WhilePaused_StaysPaused()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Play();
        h.Engine.Pause();
        int playsBeforeSeek = h.AudioRenderer.PlayCount;
        h.Engine.Seek(TimeSpan.FromSeconds(1));

        h.Engine.State.Should().Be(PlaybackState.Paused);
        h.AudioRenderer.PlayCount.Should().Be(playsBeforeSeek,
            "a paused seek must not restart audio output");
        h.Engine.Stop();
    }

    [Fact]
    public async Task Seek_WhilePlaying_RestartsAudioRendererAfterFlush()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.Play();
        int playsBeforeSeek = h.AudioRenderer.PlayCount;
        playsBeforeSeek.Should().Be(1, "Play() starts the audio renderer exactly once");

        h.Engine.Seek(TimeSpan.FromSeconds(2));

        h.AudioRenderer.FlushCount.Should().BeGreaterThanOrEqualTo(1);
        h.AudioRenderer.PlayCount.Should().Be(playsBeforeSeek + 1,
            "Flush() rebuilds the WASAPI output in Stopped state; seek must call Play() again or audio dies post-seek");
        h.Engine.State.Should().Be(PlaybackState.Playing);

        h.Engine.Stop();
    }

    [Fact]
    public async Task SetVolume_ClampsAndForwardsToRenderer()
    {
        using var h = TestEngines.Create();
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.SetVolume(2.5f);
        h.AudioRenderer.Volume.Should().Be(1f);

        h.Engine.SetVolume(-3f);
        h.AudioRenderer.Volume.Should().Be(0f);

        h.Engine.SetVolume(float.NaN);
        h.AudioRenderer.Volume.Should().Be(0f);

        h.Engine.SetVolume(0.4f);
        h.AudioRenderer.Volume.Should().Be(0.4f);
    }

    [Fact]
    public async Task SetOutputWindow_InitializesRendererOnPlay()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        var handle = new IntPtr(0x1234);
        h.Engine.SetOutputWindow(handle);
        h.Engine.Play();

        h.VideoRenderer.IsInitialized.Should().BeTrue();
        h.VideoRenderer.InitializedWindow.Should().Be(handle);
        h.VideoRenderer.ClearCount.Should().BeGreaterThanOrEqualTo(1);

        h.Engine.Stop();
    }

    [Fact]
    public async Task ResizeOutput_ForwardsToRenderer()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");
        h.Engine.SetOutputWindow(new IntPtr(0x1));
        h.Engine.Play();

        h.Engine.ResizeOutput(1280, 720);

        h.VideoRenderer.ResizeCalls.Should().ContainSingle().Which.Should().Be((1280, 720));
        h.Engine.Stop();
    }

    [Fact]
    public async Task ResizeOutput_AfterPlayBeforeWindowHandle_InitializesInsteadOfThrowing()
    {
        using var h = TestEngines.Create();
        h.AudioDecoder.Infinite = true;
        await h.Engine.OpenAsync("movie.mp4");

        // Regression: Play() created the renderer before the UI window existed,
        // leaving it unbound; the later VideoHost_Loaded resize then crashed with
        // "Video renderer has not been initialized.".
        h.Engine.Play();

        var handle = new IntPtr(0x1234);
        h.Engine.SetOutputWindow(handle);

        Action act = () => h.Engine.ResizeOutput(1280, 720);

        act.Should().NotThrow();
        h.VideoRenderer.IsInitialized.Should().BeTrue();
        h.VideoRenderer.InitializedWindow.Should().Be(handle);
        h.Engine.Stop();
    }

    [Fact]
    public async Task ResizeOutput_BeforePlayback_StoresSizeWithoutThrowing()
    {
        using var h = TestEngines.Create();
        await h.Engine.OpenAsync("movie.mp4");

        // No renderer exists yet; the size must just be stored for Initialize.
        Action act = () => h.Engine.ResizeOutput(1920, 1080);

        act.Should().NotThrow();
        h.VideoRenderer.IsInitialized.Should().BeFalse();

        h.Engine.SetOutputWindow(new IntPtr(0x2));
        h.Engine.Play();

        h.VideoRenderer.IsInitialized.Should().BeTrue();
        h.VideoRenderer.InitializedWidth.Should().Be(1920);
        h.VideoRenderer.InitializedHeight.Should().Be(1080);
        h.Engine.Stop();
    }

    [Fact]
    public async Task GetPosition_IsZeroBeforePlayback()
    {
        using var h = TestEngines.Create();
        await h.Engine.OpenAsync("movie.mp4");

        h.Engine.GetPosition().Should().Be(TimeSpan.Zero);
    }
}

using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Core.Processing;
using CATRA.Services.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Processing;

// ---------------------------------------------------------------------------
// Fakes for encoder cascade tests
// ---------------------------------------------------------------------------

/// <summary>Minimal <see cref="IVideoEncoder"/> stub for cascade tests.</summary>
internal sealed class StubVideoEncoder : IVideoEncoder
{
    public StubVideoEncoder(string name) => SelectedEncoder = name;
    public string SelectedEncoder { get; }
    public void EncodeFrame(IntPtr texture, out IntPtr packetBuffer, out int packetSize)
    { packetBuffer = IntPtr.Zero; packetSize = 0; }
    public void Flush(out IntPtr packetBuffer, out int packetSize)
    { packetBuffer = IntPtr.Zero; packetSize = 0; }
    public void Dispose() { }
}

// ---------------------------------------------------------------------------
// Tests for the REAL EncoderFallbackFactory (B2 fix: no more tautological tests)
// ---------------------------------------------------------------------------

/// <summary>
/// Tests the REAL <see cref="EncoderFallbackFactory.Create"/> cascade logic
/// using injectable seams (<see cref="EncoderFallbackFactory.AvailabilityProbe"/>
/// and <see cref="EncoderFallbackFactory.FFmpegEncoderFactory"/>) to avoid
/// spawning real FFmpeg processes. The cascade code under test is the production
/// code path — no reimplementation in test fakes.
/// </summary>
public class EncoderFallbackFactoryTests : IDisposable
{
    // Track per-test probe calls for assertions.
    private readonly List<string> _probeCalls = new();
    private readonly List<string> _ffmpegFactoryCalls = new();

    public EncoderFallbackFactoryTests()
    {
        // Default: probe says nothing available, factory creates stubs.
        EncoderFallbackFactory.AvailabilityProbe = (path, enc) =>
        {
            _probeCalls.Add(enc);
            return false; // nothing available by default
        };
        EncoderFallbackFactory.FFmpegEncoderFactory = (bridge, path, enc, w, h, br, fps) =>
        {
            _ffmpegFactoryCalls.Add(enc);
            return new StubVideoEncoder(enc);
        };
    }

    public void Dispose()
    {
        EncoderFallbackFactory.ResetTestSeams();
    }

    // --- B1/B2: AMF success path (CA-2.1) ---------------------------------

    [Fact]
    public void Create_AmfSucceeds_ReturnsAmfBridgeEncoder_NoFallback()
    {
        // AMF create succeeds → AmfBridgeEncoder returned, no probe called,
        // no FFmpeg factory called, no fallback events.
        var bridge = new FakeBridgeForCascade(); // CreateEncoder returns valid handle
        var events = new List<EncoderFallbackEventArgs>();

        var encoder = EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        encoder.Should().BeOfType<AmfBridgeEncoder>();
        encoder.SelectedEncoder.Should().Be("AMF");
        _probeCalls.Should().BeEmpty("probe not called when AMF succeeds");
        _ffmpegFactoryCalls.Should().BeEmpty("FFmpeg factory not called when AMF succeeds");
        events.Should().BeEmpty("no fallback events when AMF succeeds");
        encoder.Dispose();
    }

    // --- B1/B2: AMF -4 triggers cascade (CA-2.2) -------------------------

    [Fact]
    public void Create_AmfErrDevice_ProbesNvencThenQsv_FallsToLibx265()
    {
        // AMF fails with -4, probe says nothing available → libx265.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("AMF device error", -4)
        };
        var events = new List<EncoderFallbackEventArgs>();

        var encoder = EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        encoder.SelectedEncoder.Should().Be("libx265");
        _probeCalls.Should().BeEquivalentTo(new[] { "hevc_nvenc", "hevc_qsv" },
            options => options.WithStrictOrdering());
        _ffmpegFactoryCalls.Should().ContainSingle().Which.Should().Be("libx265");
        events.Should().HaveCount(2, "AMF→cascade + hw→libx265");
        events[0].From.Should().Be("AMF");
        events[0].Reason.Should().Contain("-4");
        events[^1].To.Should().Be("libx265");
        encoder.Dispose();
    }

    [Fact]
    public void Create_AmfErrNotImpl_FallsToNvenc()
    {
        // AMF fails with -2, probe says NVENC available → hevc_nvenc.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("AMF not compiled", -2)
        };
        EncoderFallbackFactory.AvailabilityProbe = (path, enc) =>
        {
            _probeCalls.Add(enc);
            return enc == "hevc_nvenc"; // NVENC available, QSV not
        };
        var events = new List<EncoderFallbackEventArgs>();

        var encoder = EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        encoder.SelectedEncoder.Should().Be("hevc_nvenc");
        _probeCalls.Should().Contain("hevc_nvenc");
        _ffmpegFactoryCalls.Should().ContainSingle().Which.Should().Be("hevc_nvenc");
        events.Should().HaveCount(1);
        events[0].From.Should().Be("AMF");
        events[0].Reason.Should().Contain("-2");
        encoder.Dispose();
    }

    // --- B1: NVENC probe succeeds but init fails → cascade continues ------

    [Fact]
    public void Create_AmfFails_NvencProbeOkButInitFails_FallsToQsv()
    {
        // AMF fails, NVENC probe OK but factory throws EncoderInitException
        // (simulating "Cannot load nvcuda.dll"), QSV probe OK → hevc_qsv.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("device", -4)
        };
        EncoderFallbackFactory.AvailabilityProbe = (path, enc) =>
        {
            _probeCalls.Add(enc);
            return true; // both available
        };
        EncoderFallbackFactory.FFmpegEncoderFactory = (br, p, enc, w, h, b, f) =>
        {
            _ffmpegFactoryCalls.Add(enc);
            if (enc == "hevc_nvenc")
                throw new EncoderInitException("hevc_nvenc", "Cannot load nvcuda.dll");
            return new StubVideoEncoder(enc);
        };
        var events = new List<EncoderFallbackEventArgs>();

        var encoder = EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        encoder.SelectedEncoder.Should().Be("hevc_qsv");
        _ffmpegFactoryCalls.Should().BeEquivalentTo(
            new[] { "hevc_nvenc", "hevc_qsv" },
            options => options.WithStrictOrdering());
        events.Should().HaveCount(2, "AMF→cascade + nvenc→next");
        events[1].From.Should().Be("hevc_nvenc");
        events[1].Reason.Should().Contain("nvcuda");
        encoder.Dispose();
    }

    [Fact]
    public void Create_AmfFails_NvencAndQsvBothInitFail_FallsToLibx265()
    {
        // AMF fails, both HW encoders init fail → libx265.
        // This is the EXACT scenario on AMD without CUDA/QSV.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("device", -4)
        };
        EncoderFallbackFactory.AvailabilityProbe = (path, enc) =>
        {
            _probeCalls.Add(enc);
            return true; // probe says available (compiled in)
        };
        EncoderFallbackFactory.FFmpegEncoderFactory = (br, p, enc, w, h, b, f) =>
        {
            _ffmpegFactoryCalls.Add(enc);
            if (enc == "hevc_nvenc")
                throw new EncoderInitException("hevc_nvenc", "Cannot load nvcuda.dll");
            if (enc == "hevc_qsv")
                throw new IOException("MFX session init failed: MFX_ERR_UNSUPPORTED (-9)");
            return new StubVideoEncoder(enc);
        };
        var events = new List<EncoderFallbackEventArgs>();

        var encoder = EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        encoder.SelectedEncoder.Should().Be("libx265",
            "after AMF+NVENC+QSV all fail, cascade MUST reach libx265");
        _ffmpegFactoryCalls.Should().BeEquivalentTo(
            new[] { "hevc_nvenc", "hevc_qsv", "libx265" },
            options => options.WithStrictOrdering());
        // 4 events: AMF→cascade + nvenc→next + qsv→next + lastFailed→libx265
        events.Should().HaveCount(4, "AMF→cascade + nvenc→next + qsv→next + last→libx265");
        events[^1].To.Should().Be("libx265");
        encoder.Dispose();
    }

    // --- CA-2.6: Non-fallback error codes propagate -----------------------

    [Fact]
    public void Create_AmfErrInit_NoFallback_PropagatesException()
    {
        // CATRA_ERR_INIT (-1) is NOT a fallback trigger. The pipeline converts
        // this to a failed ProcessResult. The factory must re-throw.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("init failed", -1)
        };
        var events = new List<EncoderFallbackEventArgs>();

        var act = () => EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        act.Should().Throw<NativeBridgeException>()
            .Which.ErrorCode.Should().Be(-1);
        _probeCalls.Should().BeEmpty("no probe when AMF error is not -4/-2");
        events.Should().BeEmpty("no fallback events for non-fallback errors");
    }

    [Fact]
    public void Create_AmfErrContext_NoFallback_PropagatesException()
    {
        // CATRA_ERR_CONTEXT (-5) also not a fallback trigger.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("bad context", -5)
        };

        var act = () => EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0, null);

        act.Should().Throw<NativeBridgeException>()
            .Which.ErrorCode.Should().Be(-5);
    }

    // --- Event sequence assertions ----------------------------------------

    [Fact]
    public void Create_FullCascade_EventSequenceIsCorrect()
    {
        // Verify exact event sequence through the full cascade.
        var bridge = new FakeBridgeForCascade
        {
            CreateEncoderException = new NativeBridgeException("device", -4)
        };
        int callIndex = 0;
        EncoderFallbackFactory.FFmpegEncoderFactory = (br, p, enc, w, h, b, f) =>
        {
            callIndex++;
            _ffmpegFactoryCalls.Add(enc);
            // NVENC and QSV fail on init, libx265 succeeds.
            if (enc == "hevc_nvenc")
                throw new EncoderInitException("hevc_nvenc", "no cuda");
            if (enc == "hevc_qsv")
                throw new IOException("no MFX");
            return new StubVideoEncoder(enc);
        };
        EncoderFallbackFactory.AvailabilityProbe = (path, enc) =>
        {
            _probeCalls.Add(enc);
            return true;
        };
        var events = new List<EncoderFallbackEventArgs>();

        var encoder = EncoderFallbackFactory.Create(
            bridge, "ffmpeg", 1920, 1080, 20000, 24.0,
            args => events.Add(args));

        encoder.SelectedEncoder.Should().Be("libx265");

        // Exact event sequence:
        // 1. AMF → cascade (AMF failed with -4)
        // 2. hevc_nvenc → next encoder (init failed)
        // 3. hevc_qsv → next encoder (init failed)
        // 4. hevc_qsv → libx265 (final software fallback, From = lastFailedEncoder)
        events.Should().HaveCount(4);
        events[0].From.Should().Be("AMF");
        events[0].To.Should().Be("FFmpeg cascade");
        events[1].From.Should().Be("hevc_nvenc");
        events[1].To.Should().Be("next encoder");
        events[2].From.Should().Be("hevc_qsv");
        events[2].To.Should().Be("next encoder");
        events[3].From.Should().Be("hevc_qsv");
        events[3].To.Should().Be("libx265");
        encoder.Dispose();
    }

    // --- Pipeline integration with REAL factory ---------------------------

    [Fact]
    public async Task Pipeline_WithRealFactory_AmfFails_FallsToLibx265()
    {
        // End-to-end: pipeline uses real EncoderFallbackFactory with test seams.
        // AMF fails → cascade reaches libx265.
        string folder = CreateTempFolder();
        try
        {
            var bridge = new FakeNativeBridge
            {
                ThrowOnCreateEncoder = new NativeBridgeException("AMF device", -4)
            };
            var muxer = new FakeAudioMuxer();
            var spec = new FakeDecoderSpec
            {
                Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 3),
                FrameCount = 3,
            };

            // Use real ProductionEncoderFactory path via injected IVideoEncoderFactory
            // that delegates to the real EncoderFallbackFactory with test seams.
            var factory = new TestableEncoderFactory();

            var pipeline = new ProcessingPipeline(
                () => bridge,
                () => new FakeFrameDecoder(spec),
                muxer,
                frameTimeout: null,
                progressFrameInterval: 1,
                encoderFactory: factory);

            var emittedEvents = new List<EncoderFallbackEventArgs>();
            pipeline.EncoderFallback += (_, args) => emittedEvents.Add(args);

            var config = new PipelineConfig(
                ProcessProfile.Local, 1920, 1080, 24, 20_000, "rife", "fsr1", folder);
            var ep = new Episode
            {
                Id = 1,
                FilePath = Path.Combine(folder, "s.mp4"),
                FileName = "s.mp4"
            };

            var result = await pipeline.ProcessAsync(ep, config,
                new CapturingProgress(), CancellationToken.None);

            result.Success.Should().BeTrue();
            result.SelectedEncoder.Should().Be("libx265");
            emittedEvents.Should().NotBeEmpty("fallback events should fire");
        }
        finally
        {
            CleanupTempFolder(folder);
        }
    }

    // --- EncoderFallbackEventArgs contract --------------------------------

    [Fact]
    public void EncoderFallbackEventArgs_HasCorrectProperties()
    {
        var args = new EncoderFallbackEventArgs("AMF", "hevc_nvenc", "AMF unavailable");
        args.From.Should().Be("AMF");
        args.To.Should().Be("hevc_nvenc");
        args.Reason.Should().Be("AMF unavailable");
    }

    // --- AmfBridgeEncoder unit test ---------------------------------------

    [Fact]
    public void AmfBridgeEncoder_SelectedEncoder_IsAMF()
    {
        var bridge = new FakeBridgeForCascade();
        var encoder = new AmfBridgeEncoder(bridge, new IntPtr(42));
        encoder.SelectedEncoder.Should().Be("AMF");
        encoder.Dispose();
        bridge.DestroyEncoderCalled.Should().BeTrue();
    }

    // --- BuildArguments includes -pix_fmt yuv420p (M1) --------------------

    [Fact]
    public void BuildArguments_IncludesYuv420pPixelFormat()
    {
        string args = FFmpegCliEncoder.BuildArguments("libx265", 1920, 1080, 20000, 24.0);
        args.Should().Contain("-pix_fmt yuv420p",
            "M1: output must use yuv420p to strip alpha (yuva420p)");
        args.Should().Contain("-pix_fmt bgra",
            "input format must be bgra");
        args.Should().Contain("-c:v libx265");
    }

    [Fact]
    public void BuildArguments_NvencOptions()
    {
        string args = FFmpegCliEncoder.BuildArguments("hevc_nvenc", 1920, 1080, 20000, 60.0);
        args.Should().Contain("-preset p4");
        args.Should().Contain("-rc cbr");
        args.Should().Contain("-c:v hevc_nvenc");
    }

    // --- helpers -----------------------------------------------------------

    private static string CreateTempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "catra-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch { /* best effort */ }
    }
}

/// <summary>
/// <see cref="IVideoEncoderFactory"/> that delegates to the REAL
/// <see cref="EncoderFallbackFactory.Create"/> with test seams already
/// configured by the test fixture. Used for pipeline integration tests.
/// </summary>
internal sealed class TestableEncoderFactory : IVideoEncoderFactory
{
    public IVideoEncoder Create(
        INativeBridge bridge, int width, int height,
        int bitrateKbps, double fps,
        Action<EncoderFallbackEventArgs>? onFallback)
    {
        return EncoderFallbackFactory.Create(
            bridge, "ffmpeg", width, height, bitrateKbps, fps, onFallback);
    }
}

/// <summary>
/// Minimal <see cref="INativeBridge"/> for cascade unit tests. Only implements
/// the encoder-related methods; everything else throws NotImplementedException.
/// </summary>
internal sealed class FakeBridgeForCascade : INativeBridge
{
    public bool IsAvailable => true;
    public bool IsInitialized => true;
    public Exception? CreateEncoderException { get; set; }
    public bool DestroyEncoderCalled { get; private set; }

    public void Initialize(IntPtr d3d11Device) { }
    public void Shutdown() { }
    public bool IsFsr4Available() => true;
    public bool IsFfxAvailable() => true;
    public int GetUpscaleMode() => 2;
    public int GetInterpMethod() => 1;
    public IntPtr CreateInterpolation(int sw, int sh, double sfps, double tfps, int method) => IntPtr.Zero;
    public int ProcessInterpolation(IntPtr ctx, IntPtr a, IntPtr b, out IntPtr buf) { buf = IntPtr.Zero; return 0; }
    public void DestroyInterpolation(IntPtr ctx) { }
    public IntPtr CreateUpscaler(int sw, int sh, int dw, int dh, int method) => IntPtr.Zero;
    public IntPtr ProcessUpscale(IntPtr ctx, IntPtr src) => IntPtr.Zero;
    public void DestroyUpscaler(IntPtr ctx) { }
    public int SubmitUpscaleAsync(IntPtr ctx, IntPtr src) => 0;
    public IntPtr PollUpscaleResult(IntPtr ctx, int ticket) => IntPtr.Zero;
    public int GetUpscalePendingCount(IntPtr ctx) => 0;

    public IntPtr CreateEncoder(int w, int h, int bitrate, double fps)
    {
        if (CreateEncoderException is not null) throw CreateEncoderException;
        return new IntPtr(1);
    }

    public void EncodeFrame(IntPtr ctx, IntPtr tex, out IntPtr buf, out int size)
    { buf = IntPtr.Zero; size = 0; }

    public void FlushEncoder(IntPtr ctx, out IntPtr buf, out int size)
    { buf = IntPtr.Zero; size = 0; }

    public void DestroyEncoder(IntPtr ctx) => DestroyEncoderCalled = true;

    public void ReadbackTextureToCpu(IntPtr texture, out byte[] pixels,
        out uint dxgiFormat, out uint rowPitch)
    { pixels = []; dxgiFormat = 0; rowPitch = 0; }

    public void ReleaseTexture(IntPtr texture) { }
    public void FreeNativeArray(IntPtr ptr) { }
    public void Dispose() { }
}

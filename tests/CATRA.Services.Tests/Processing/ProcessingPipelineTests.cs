using System.Runtime.InteropServices;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Core.Processing;
using CATRA.Data.Database;
using CATRA.Data.Repositories;
using CATRA.Services.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Processing;

// ---------------------------------------------------------------------------
// Fakes — let the pipeline be exercised end-to-end with no GPU / native DLL /
// ffmpeg binary. Mirror the FakeNativeLibrary pattern from NativeBridgeTests.
// ---------------------------------------------------------------------------

/// <summary>Shared configuration for every <see cref="FakeFrameDecoder"/> in a run.</summary>
internal sealed class FakeDecoderSpec
{
    public FrameSourceMetadata Metadata { get; set; } =
        new(Fps: 24, Width: 1280, Height: 720, Duration: TimeSpan.FromSeconds(10), TotalFrames: 10);

    public int FrameCount { get; set; } = 10;

    /// <summary>File paths that make <see cref="FakeFrameDecoder.Open"/> throw.</summary>
    public HashSet<string> FailOnOpenPaths { get; } = new();

    /// <summary>Invoked with the 1-based frame index right after a frame is emitted.</summary>
    public Action<int>? OnFrameRead { get; set; }

    public int OpenCallCount { get; set; }
}

/// <summary>In-memory <see cref="IFrameDecoder"/>: emits N fake texture pointers.</summary>
internal sealed class FakeFrameDecoder : IFrameDecoder
{
    private readonly FakeDecoderSpec _spec;
    private readonly List<IntPtr> _released = new();
    private int _readIndex;
    private bool _opened;
    private bool _disposed;

    public FakeFrameDecoder(FakeDecoderSpec spec) => _spec = spec;

    public FrameSourceMetadata Metadata { get; private set; } = null!;

    public IntPtr D3D11DevicePtr => IntPtr.Zero; // fake: no real GPU device

    public string? OpenedPath { get; private set; }

    public bool Disposed => _disposed;

    public IReadOnlyList<IntPtr> ReleasedFrames => _released;

    public void Open(string filePath)
    {
        _spec.OpenCallCount++;
        if (_spec.FailOnOpenPaths.Contains(filePath))
        {
            throw new InvalidOperationException($"decode failed for {filePath}");
        }

        OpenedPath = filePath;
        Metadata = _spec.Metadata;
        _readIndex = 0;
        _opened = true;
    }

    public bool TryReadFrame(out IntPtr texture)
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Decoder has not been opened.");
        }

        if (_readIndex >= _spec.FrameCount)
        {
            texture = IntPtr.Zero;
            return false;
        }

        _readIndex++;
        texture = new IntPtr(0x1000 + _readIndex);
        _spec.OnFrameRead?.Invoke(_readIndex);
        return true;
    }

    public void ReleaseFrame(IntPtr texture)
    {
        if (texture != IntPtr.Zero)
        {
            _released.Add(texture);
        }
    }

    public void Dispose() => _disposed = true;
}

/// <summary>In-memory <see cref="IAudioMuxer"/>: records calls, produces the final file.</summary>
internal sealed class FakeAudioMuxer : IAudioMuxer
{
    public int MuxCallCount { get; private set; }

    public bool Fail { get; set; }

    public List<(string Source, string Video, string Final, double Fps)> Calls { get; } = new();

    public void Mux(string sourcePath, string encodedVideoPath, string finalOutputPath, double videoFps)
    {
        MuxCallCount++;
        Calls.Add((sourcePath, encodedVideoPath, finalOutputPath, videoFps));
        if (Fail)
        {
            throw new IOException("mux failed");
        }

        // Simulate producing the final MP4 from the encoded (video-only) file.
        File.Copy(encodedVideoPath, finalOutputPath, overwrite: true);
    }
}

/// <summary>
/// In-memory <see cref="INativeBridge"/>: counts create/destroy/process calls, logs
/// their order, and hands back real unmanaged buffers so the pipeline's marshalling
/// (Marshal.ReadIntPtr / Marshal.Copy / Marshal.FreeHGlobal) works for real.
/// </summary>
internal sealed class FakeNativeBridge : INativeBridge
{
    private IntPtr _encodeBuffer;
    private int _nextContext = 1;
    private long _nextIntermediate = 0x9000;

    public bool IsAvailable { get; set; } = true;

    public bool IsInitialized { get; private set; }

    // Configurable behaviour.
    public int InterpFramesPerPair { get; set; } = 2;
    public int EncodePacketSize { get; set; } = 4;
    public int FlushPacketSize { get; set; } = 4;
    public int EncodeFrameDelayMs { get; set; }
    public Exception? ThrowOnEncodeFrame { get; set; }
    public Exception? ThrowOnInterpCreate { get; set; }

    /// <summary>
    /// Thrown by <see cref="CreateUpscaler"/> ONLY when method == 2 (CATRA_UPSCALE_FSR4)
    /// — story 03 FSR 4 -> FSR 1 fallback tests.
    /// </summary>
    public Exception? ThrowOnUpscaleCreateFsr4 { get; set; }

    // Call counts.
    public int InterpCreateCount { get; private set; }
    public int InterpDestroyCount { get; private set; }
    public int InterpProcessCount { get; private set; }
    public int UpscaleCreateCount { get; private set; }
    public int UpscaleDestroyCount { get; private set; }
    public int UpscaleProcessCount { get; private set; }
    public int EncodeCreateCount { get; private set; }
    public int EncodeDestroyCount { get; private set; }
    public int EncodeFrameCount { get; private set; }
    public int EncodeFlushCount { get; private set; }

    // Resource-release bookkeeping (ST-17 leak regression): counts allocations
    // handed out vs. releases/free actually performed by the pipeline.
    public int ReleaseTextureCount { get; private set; }
    public int FreeNativeArrayCount { get; private set; }
    public List<IntPtr> ReleasedTextures { get; } = new();
    public List<IntPtr> FreedArrays { get; } = new();

    /// <summary>Upscale method codes passed to each CreateUpscaler call, in order (story 03 fallback).</summary>
    public List<int> UpscaleCreateMethods { get; } = new();

    // Last arguments (for RN-07 / forwarding assertions).
    public double LastInterpSrcFps { get; private set; }
    public double LastInterpTargetFps { get; private set; }
    public int LastUpscaleSrcHeight { get; private set; }
    public int LastUpscaleDstHeight { get; private set; }
    public int LastEncodeWidth { get; private set; }
    public int LastEncodeHeight { get; private set; }
    public int LastEncodeBitrate { get; private set; }
    public double LastEncodeFps { get; private set; }

    /// <summary>Ordered log of context operations (for flow assertions).</summary>
    public List<string> Calls { get; } = new();

    public void Initialize(IntPtr d3d11Device) => IsInitialized = true;

    public void Shutdown() => IsInitialized = false;

    public bool IsFsr4Available() => true;

    public int GetUpscaleMode() => 2;

    public int GetInterpMethod() => 1;

    public IntPtr CreateInterpolation(int srcWidth, int srcHeight, double srcFps, double targetFps, int method)
    {
        InterpCreateCount++;
        if (ThrowOnInterpCreate is not null)
        {
            throw ThrowOnInterpCreate;
        }
        LastInterpSrcFps = srcFps;
        LastInterpTargetFps = targetFps;
        Calls.Add("interp_create");
        return new IntPtr(_nextContext++);
    }

    public int ProcessInterpolation(IntPtr context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer)
    {
        InterpProcessCount++;
        Calls.Add("interp_process");

        int count = InterpFramesPerPair;
        if (count <= 0)
        {
            framesBuffer = IntPtr.Zero;
            return 0;
        }

        // Real unmanaged array so the pipeline's Marshal.ReadIntPtr works; the pipeline
        // frees it through FreeNativeArray (caller-owned per the native contract), which
        // the fake backs with Marshal.FreeHGlobal to match this AllocHGlobal. Each
        // intermediate gets a globally-unique pointer (like a real AddRef'd texture) so
        // release-accounting assertions can detect any double-release.
        framesBuffer = Marshal.AllocHGlobal(count * IntPtr.Size);
        for (int i = 0; i < count; i++)
        {
            Marshal.WriteIntPtr(framesBuffer, i * IntPtr.Size, new IntPtr(_nextIntermediate++));
        }

        return count;
    }

    public void DestroyInterpolation(IntPtr context)
    {
        InterpDestroyCount++;
        Calls.Add("interp_destroy");
    }

    public IntPtr CreateUpscaler(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method)
    {
        UpscaleCreateCount++;
        UpscaleCreateMethods.Add(method);
        LastUpscaleSrcHeight = srcHeight;
        LastUpscaleDstHeight = dstHeight;
        if (method == 2 && ThrowOnUpscaleCreateFsr4 is not null) // 2 = CATRA_UPSCALE_FSR4
        {
            throw ThrowOnUpscaleCreateFsr4;
        }
        Calls.Add("upscale_create");
        return new IntPtr(_nextContext++);
    }

    public IntPtr ProcessUpscale(IntPtr context, IntPtr srcTexture)
    {
        UpscaleProcessCount++;
        Calls.Add("upscale_process");
        return new IntPtr(0x80000 + srcTexture.ToInt64());
    }

    public void DestroyUpscaler(IntPtr context)
    {
        UpscaleDestroyCount++;
        Calls.Add("upscale_destroy");
    }

    public IntPtr CreateEncoder(int width, int height, int bitrateKbps, double fps)
    {
        EncodeCreateCount++;
        LastEncodeWidth = width;
        LastEncodeHeight = height;
        LastEncodeBitrate = bitrateKbps;
        LastEncodeFps = fps;
        Calls.Add("encode_create");
        return new IntPtr(_nextContext++);
    }

    public void EncodeFrame(IntPtr context, IntPtr texture, out IntPtr packetBuffer, out int packetSize)
    {
        if (ThrowOnEncodeFrame is not null)
        {
            throw ThrowOnEncodeFrame;
        }

        if (EncodeFrameDelayMs > 0)
        {
            Thread.Sleep(EncodeFrameDelayMs);
        }

        EncodeFrameCount++;
        Calls.Add("encode_frame");
        EnsureEncodeBuffer();
        packetBuffer = _encodeBuffer;
        packetSize = EncodePacketSize;
    }

    public void FlushEncoder(IntPtr context, out IntPtr packetBuffer, out int packetSize)
    {
        EncodeFlushCount++;
        Calls.Add("encode_flush");
        EnsureEncodeBuffer();
        packetBuffer = _encodeBuffer;
        packetSize = FlushPacketSize;
    }

    public void DestroyEncoder(IntPtr context)
    {
        EncodeDestroyCount++;
        Calls.Add("encode_destroy");
    }

    public void ReleaseTexture(IntPtr texture)
    {
        ReleaseTextureCount++;
        ReleasedTextures.Add(texture);
        Calls.Add("release_texture");
    }

    public void FreeNativeArray(IntPtr ptr)
    {
        FreeNativeArrayCount++;
        FreedArrays.Add(ptr);
        Calls.Add("free_native_array");

        // Matches the AllocHGlobal used in ProcessInterpolation (the fake stands in
        // for the native `new[]` array; the pipeline only ever calls this, never
        // Marshal.FreeHGlobal, so the heap stays consistent).
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void EnsureEncodeBuffer()
    {
        int size = Math.Max(1, Math.Max(EncodePacketSize, FlushPacketSize));
        if (_encodeBuffer == IntPtr.Zero)
        {
            _encodeBuffer = Marshal.AllocHGlobal(size);
        }

        for (int i = 0; i < size; i++)
        {
            Marshal.WriteByte(_encodeBuffer, i, 0xAB);
        }
    }

    public void Dispose()
    {
        if (_encodeBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_encodeBuffer);
            _encodeBuffer = IntPtr.Zero;
        }
    }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> that captures every report.</summary>
internal sealed class CapturingProgress : IProgress<PipelineProgress>
{
    private readonly List<PipelineProgress> _reports = new();

    public IReadOnlyList<PipelineProgress> Reports
    {
        get
        {
            lock (_reports)
            {
                return _reports.ToList();
            }
        }
    }

    public void Report(PipelineProgress value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

/// <summary>
/// Orchestration tests for <see cref="ProcessingPipeline"/> (ST-17). The full
/// decode → interp → upscale → encode → mux flow is driven through fakes — no GPU,
/// native DLL or ffmpeg binary. Covers RN-07 skip decisions, progress/ETA,
/// cancellation, error cleanup, batch isolation and native-context lifecycle.
/// </summary>
public class ProcessingPipelineTests : IDisposable
{
    private readonly List<string> _tempFolders = new();

    public void Dispose()
    {
        foreach (string folder in _tempFolders)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    // --- helpers -----------------------------------------------------------

    private string NewTempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "catra-st17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempFolders.Add(dir);
        return dir;
    }

    private static PipelineConfig LocalConfig(string folder) =>
        new(ProcessProfile.Local, 1920, 1080, 135, 20_000, "rife", "fsr4", folder);

    private static PipelineConfig DlnaConfig(string folder) =>
        new(ProcessProfile.Dlna, 3840, 2160, 55, 45_000, "rife", "fsr4", folder);

    private static Episode Ep(int id, string path) =>
        new() { Id = id, FilePath = path, FileName = Path.GetFileName(path) };

    private static (ProcessingPipeline Pipeline, FakeNativeBridge Bridge, FakeAudioMuxer Muxer, FakeDecoderSpec Spec)
        Build(
            FakeDecoderSpec? spec = null,
            int progressInterval = 1,
            TimeSpan? frameTimeout = null)
    {
        var bridge = new FakeNativeBridge();
        var muxer = new FakeAudioMuxer();
        var shared = spec ?? new FakeDecoderSpec();
        var pipeline = new ProcessingPipeline(
            bridge,
            () => new FakeFrameDecoder(shared),
            muxer,
            frameTimeout,
            progressInterval);
        return (pipeline, bridge, muxer, shared);
    }

    // --- contract ----------------------------------------------------------

    [Fact]
    public void Pipeline_ImplementsIProcessingPipeline()
    {
        typeof(ProcessingPipeline).Should().Implement<IProcessingPipeline>();
    }

    [Fact]
    public void FrameDecoder_ImplementsIFrameDecoder()
    {
        typeof(FrameDecoder).Should().Implement<IFrameDecoder>();
    }

    [Fact]
    public void AudioMuxer_ImplementsIAudioMuxer()
    {
        typeof(AudioMuxer).Should().Implement<IAudioMuxer>();
    }

    // --- RN-07 skip decisions ----------------------------------------------

    [Theory]
    [InlineData(24, 720, 135, 1080, true, true)]    // 720p24 → local: interp ✓ upscale ✓
    [InlineData(60, 1080, 135, 1080, true, false)]   // 1080p60 → local: interp ✓ upscale ✗
    [InlineData(135, 1080, 135, 1080, false, false)] // already at target: both skipped
    [InlineData(24, 2160, 55, 2160, true, false)]    // 4K24 → dlna: interp ✓ upscale ✗
    [InlineData(24, 720, 55, 2160, true, true)]      // 720p24 → dlna: interp ✓ upscale ✓
    [InlineData(120, 720, 55, 2160, false, true)]    // fps above target, res below: interp ✗ upscale ✓
    public async Task Rn07_SkipDecisions_CreateOnlyActiveContexts(
        double srcFps, int srcHeight, double targetFps, int targetHeight, bool expectInterp, bool expectUpscale)
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(srcFps, 1280, srcHeight, TimeSpan.FromSeconds(2), 3),
            FrameCount = 3,
        };
        var (pipeline, bridge, _, _) = Build(spec);
        var config = new PipelineConfig(ProcessProfile.Local, 1920, targetHeight, targetFps, 20_000, "rife", "fsr4", folder);

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "src.mp4")), config, new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.InterpCreateCount.Should().Be(expectInterp ? 1 : 0, "interp runs only when src_fps < target_fps");
        bridge.UpscaleCreateCount.Should().Be(expectUpscale ? 1 : 0, "upscale runs only when src_height < target_height");
        bridge.EncodeCreateCount.Should().Be(1, "the encoder always runs");
    }

    [Fact]
    public async Task Rn07_ForwardsSourceAndTargetParametersToNative()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 3),
            FrameCount = 3,
        };
        var (pipeline, bridge, _, _) = Build(spec);

        await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        bridge.LastInterpSrcFps.Should().Be(24);
        bridge.LastInterpTargetFps.Should().Be(135);
        bridge.LastUpscaleSrcHeight.Should().Be(720);
        bridge.LastUpscaleDstHeight.Should().Be(1080);
        bridge.LastEncodeWidth.Should().Be(1920);
        bridge.LastEncodeHeight.Should().Be(1080);
        bridge.LastEncodeBitrate.Should().Be(20_000);
        // ST-30 floor mode: encoder fps = srcFps * floor(target/src) = 24 * 5 = 120.
        bridge.LastEncodeFps.Should().Be(120);
    }

    // --- story 03: FSR 4 -> FSR 1 graceful fallback + D-PO-3 default -------

    [Fact]
    public async Task Upscale_Fsr4CreateFails_RetriesWithFsr1_AndCompletes()
    {
        // Story 03 (2ª linha de defesa): when the FSR 4 upscaler cannot be created
        // (NativeBridgeException — e.g. FFX runtime absent and the native downgrade
        // still surfaced as an error), the pipeline must retry ONCE with FSR 1 and
        // the export still completes. Without the retry the whole run fails.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 3),
            FrameCount = 3,
        };
        var (pipeline, bridge, _, _) = Build(spec);
        bridge.ThrowOnUpscaleCreateFsr4 = new NativeBridgeException("FSR 4 unavailable (FFX runtime missing)", -2);

        // "fsr4" requested; target fps == source fps keeps interpolation OUT so the
        // upscale-call accounting stays deterministic (one ProcessUpscale per frame).
        var config = new PipelineConfig(ProcessProfile.Local, 1920, 1080, 24, 20_000, "rife", "fsr4", folder);

        ProcessResult result = await pipeline.ProcessAsync(
            Ep(1, Path.Combine(folder, "s.mp4")), config, new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue("the FSR 4 create failure must trigger the FSR 1 retry, not fail the export");
        bridge.UpscaleCreateMethods.Should().Equal(new[] { 2, 1 }, "first attempt uses FSR 4 (2), the retry uses FSR 1 (1)");
        bridge.UpscaleCreateCount.Should().Be(2);
        bridge.UpscaleProcessCount.Should().Be(3, "every frame is upscaled by the fallback FSR 1 context");
        bridge.UpscaleDestroyCount.Should().Be(1, "only the successful FSR 1 context is destroyed in cleanup");
    }

    [Fact]
    public async Task QueueService_UpscaleSettingMissing_DefaultsToFsr1()
    {
        // D-PO-3 (story 03): FSR 1 is the export default. Two layers agree:
        // DatabaseInitializer seeds "upscale_method" = "fsr1" on fresh installs, and
        // ProcessingQueueService.BuildConfig falls back to "fsr1" when the setting is
        // absent. This test asserts the seed AND forces the missing-setting path so
        // the queue service's own default is what supplies the value to the pipeline.
        string folder = NewTempFolder();
        using var database = new DatabaseConnection(Path.Combine(folder, "queue-default.db"));
        new DatabaseInitializer(database).Initialize();

        var settings = new AppSettingsRepository(database);
        settings.Get("upscale_method").Should().Be("fsr1", "DatabaseInitializer must seed the D-PO-3 default");

        // Remove the seeded row to force the queue service's `?? "fsr1"` fallback.
        database.Connection.Execute("DELETE FROM AppSettings WHERE Key = 'upscale_method';");

        var episodes = new EpisodeRepository(database);
        var jobs = new ProcessJobRepository(database);
        var processedFiles = new ProcessedFileRepository(database);
        settings.Set("processed_folder", Path.Combine(folder, "processed"));

        var fakePipeline = new FakeProcessingPipeline();
        using var service = new ProcessingQueueService(jobs, episodes, processedFiles, fakePipeline, settings);

        Episode ep = episodes.Insert(new Episode
        {
            MediaItemId = 1,
            EpisodeNumber = 1,
            FileName = "s1-ep1.mkv",
            FilePath = Path.Combine(folder, "s1-ep1.mkv"),
        });

        await service.StartAsync();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ProcessJob> terminal = (_, _) => tcs.TrySetResult();
        service.JobCompleted += terminal;
        service.JobFailed += terminal;

        await service.EnqueueAsync(new List<int> { ep.Id }, ProcessProfile.Local);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fakePipeline.Calls.Should().HaveCount(1);
        fakePipeline.Calls.Single().Config.UpscaleMethod.Should().Be("fsr1",
            "D-PO-3: when the setting is absent the queue service defaults the export to FSR 1");
    }

    // --- bugfix_06: mux must receive the exact encode fps (A/V sync) --------

    [Fact]
    public async Task Bugfix06_Mux_ReceivesEffectiveInterpFps()
    {
        // 24 fps source, 135 fps target -> floor(135/24)=5 -> effective 120 fps.
        // The mux must get the SAME rate the encoder used, otherwise the muxed
        // video timestamps drift against the audio.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 3),
            FrameCount = 3,
        };
        var (pipeline, bridge, muxer, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(
            Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.LastEncodeFps.Should().Be(120);
        muxer.MuxCallCount.Should().Be(1);
        muxer.Calls[0].Fps.Should().Be(bridge.LastEncodeFps,
            "the mux must timestamp the video with the exact encode fps");
    }

    [Fact]
    public async Task Bugfix06_Mux_PassThrough_ReceivesSourceFps_NotTargetFps()
    {
        // No interpolation (source already meets the target): frames arrive at the
        // SOURCE fps, so both encoder and mux must use it — using TargetFps would
        // stretch the video track against the audio by TargetFps/srcFps.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(60, 1920, 1080, TimeSpan.FromSeconds(2), 4),
            FrameCount = 4,
        };
        var (pipeline, bridge, muxer, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(
            Ep(2, Path.Combine(folder, "s.mp4")), DlnaConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.InterpCreateCount.Should().Be(0, "source fps (60) already meets the dlna target (55)");
        bridge.LastEncodeFps.Should().Be(60, "pass-through encodes at the source rate");
        muxer.Calls[0].Fps.Should().Be(60);
    }

    // --- graceful degradation: interp unavailable ----------------------------

    [Fact]
    public async Task GracefulDegradation_InterpCreateFails_PipelineContinuesWithoutInterp()
    {
        // Simulates the real-world scenario: RIFE model missing, ORT version
        // mismatch, or GPU init failure. The pipeline must catch the
        // NativeBridgeException, skip interpolation, and still produce output
        // (upscale + encode + mux).
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 3),
            FrameCount = 3,
        };
        var (pipeline, bridge, _, _) = Build(spec);
        bridge.ThrowOnInterpCreate = new NativeBridgeException(
            "Native call 'catra_interp_create' failed with error code -1.", -1);

        ProcessResult result = await pipeline.ProcessAsync(
            Ep(1, Path.Combine(folder, "src.mp4")),
            LocalConfig(folder),
            new CapturingProgress(),
            CancellationToken.None);

        result.Success.Should().BeTrue("pipeline degrades gracefully when interp is unavailable");
        bridge.InterpCreateCount.Should().Be(1, "interp create was attempted");
        bridge.InterpProcessCount.Should().Be(0, "no interp processing when create failed");
        bridge.InterpDestroyCount.Should().Be(0, "no interp destroy when create failed");
        bridge.UpscaleCreateCount.Should().Be(1, "upscale still runs after interp failure");
        bridge.EncodeCreateCount.Should().Be(1, "encode still runs after interp failure");
    }

    // --- full flow orchestration -------------------------------------------

    [Fact]
    public async Task FullFlow_DecodeInterpUpscaleEncodeMux_AllOrchestrated()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 5),
            FrameCount = 5,
        };
        var (pipeline, bridge, muxer, _) = Build(spec);
        bridge.InterpFramesPerPair = 2;

        ProcessResult result = await pipeline.ProcessAsync(Ep(7, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputPath.Should().Be(Path.Combine(folder, "7_local.mp4"));
        File.Exists(result.OutputPath).Should().BeTrue();
        result.OutputSizeBytes.Should().BeGreaterThan(0);
        result.ErrorMessage.Should().BeNull();

        // Contexts created once each.
        bridge.InterpCreateCount.Should().Be(1);
        bridge.UpscaleCreateCount.Should().Be(1);
        bridge.EncodeCreateCount.Should().Be(1);

        // Interpolation fed one A/B pair per adjacent frame gap (5 frames → 4 pairs).
        bridge.InterpProcessCount.Should().Be(4);

        // Every source frame encoded once (5) + 2 intermediates per pair (4×2 = 8) = 13.
        bridge.EncodeFrameCount.Should().Be(13);
        bridge.UpscaleProcessCount.Should().Be(13, "every encoded frame is upscaled first");
        bridge.EncodeFlushCount.Should().Be(1);

        muxer.MuxCallCount.Should().Be(1);

        // Creation precedes processing; flush precedes teardown.
        bridge.Calls.IndexOf("interp_create").Should().BeLessThan(bridge.Calls.IndexOf("interp_process"));
        bridge.Calls.IndexOf("encode_create").Should().BeLessThan(bridge.Calls.IndexOf("encode_frame"));
        bridge.Calls.IndexOf("encode_frame").Should().BeLessThan(bridge.Calls.IndexOf("encode_flush"));
    }

    [Fact]
    public async Task FullFlow_NoInterpNoUpscale_EncodesSourceFramesDirectly()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(135, 1920, 1080, TimeSpan.FromSeconds(2), 4),
            FrameCount = 4,
        };
        var (pipeline, bridge, muxer, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(Ep(3, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.InterpCreateCount.Should().Be(0);
        bridge.UpscaleCreateCount.Should().Be(0);
        bridge.InterpProcessCount.Should().Be(0);
        bridge.UpscaleProcessCount.Should().Be(0);
        bridge.EncodeFrameCount.Should().Be(4, "each source frame is encoded directly");
        muxer.MuxCallCount.Should().Be(1);
    }

    [Fact]
    public async Task FullFlow_DlnaProfile_NamesOutputDlna()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 2 };
        var (pipeline, _, _, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(Ep(9, Path.Combine(folder, "s.mp4")), DlnaConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputPath.Should().Be(Path.Combine(folder, "9_dlna.mp4"));
    }

    // --- resource-leak regression (ST-17 ownership fix) --------------------
    //
    // The bridge hands back caller-owned native resources: every ProcessUpscale
    // destination texture and every ProcessInterpolation intermediate texture + the
    // frame array itself. The encoder reads them zero-copy and never releases them,
    // so the pipeline must release/free each exactly once — on success AND on error /
    // cancellation. These tests count allocations vs. releases to prove zero leak.

    [Fact]
    public async Task Leak_UpscaleFlow_EveryUpscaledTextureIsReleased()
    {
        // Upscale ON, interp OFF (src fps already meets target): the only caller-owned
        // textures are the upscale destinations, so ReleaseTexture must match ProcessUpscale.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(135, 1280, 720, TimeSpan.FromSeconds(2), 4),
            FrameCount = 4,
        };
        var (pipeline, bridge, _, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.UpscaleCreateCount.Should().Be(1);
        bridge.InterpCreateCount.Should().Be(0, "src fps >= target fps disables interp");

        // Zero leak: one release per upscaled texture, each released exactly once.
        bridge.UpscaleProcessCount.Should().Be(4, "each of the 4 source frames is upscaled");
        bridge.ReleaseTextureCount.Should().Be(bridge.UpscaleProcessCount, "every caller-owned upscale output is released");
        bridge.ReleasedTextures.Should().OnlyHaveUniqueItems("no texture is released twice");
        bridge.FreeNativeArrayCount.Should().Be(0, "no interp arrays are allocated in this flow");
    }

    [Fact]
    public async Task Leak_InterpFlow_EveryIntermediateAndArrayIsReleased()
    {
        // Interp ON, upscale OFF (src height already meets target): the only caller-owned
        // resources are the intermediate textures + the per-pair arrays, so ReleaseTexture
        // must match the intermediate count and FreeNativeArray the pair count.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 1080, TimeSpan.FromSeconds(2), 5),
            FrameCount = 5,
        };
        var (pipeline, bridge, _, _) = Build(spec);
        bridge.InterpFramesPerPair = 2;

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.UpscaleCreateCount.Should().Be(0, "src height >= target height disables upscale");
        bridge.UpscaleProcessCount.Should().Be(0);

        // 5 frames -> 4 A/B pairs; each yields 2 intermediates.
        bridge.InterpProcessCount.Should().Be(4);
        int intermediates = bridge.InterpProcessCount * bridge.InterpFramesPerPair; // 8
        intermediates.Should().Be(8);

        // Zero leak: every intermediate texture released once, every array freed once.
        bridge.ReleaseTextureCount.Should().Be(intermediates, "every intermediate texture is released");
        bridge.ReleasedTextures.Should().OnlyHaveUniqueItems("no intermediate is released twice");
        bridge.FreeNativeArrayCount.Should().Be(bridge.InterpProcessCount, "every frame array is freed via the native deallocator");
    }

    [Fact]
    public async Task Leak_InterpPlusUpscaleFlow_AllCallerOwnedResourcesReleased()
    {
        // Both stages ON: caller-owned resources are (a) one upscale destination per encoded
        // frame and (b) the intermediate textures + arrays. The release count must equal the
        // upscale outputs PLUS the intermediates — nothing leaks, nothing double-freed.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(2), 5),
            FrameCount = 5,
        };
        var (pipeline, bridge, _, _) = Build(spec);
        bridge.InterpFramesPerPair = 2;

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();

        // 5 source frames + 4 pairs x 2 intermediates = 13 encoded (each upscaled).
        bridge.EncodeFrameCount.Should().Be(13);
        bridge.UpscaleProcessCount.Should().Be(13);
        int intermediates = bridge.InterpProcessCount * bridge.InterpFramesPerPair; // 4 x 2 = 8

        // Every upscale output (13) AND every intermediate (8) is released exactly once.
        bridge.ReleaseTextureCount.Should().Be(bridge.UpscaleProcessCount + intermediates);
        bridge.ReleasedTextures.Should().OnlyHaveUniqueItems();
        bridge.FreeNativeArrayCount.Should().Be(bridge.InterpProcessCount);
    }

    [Fact]
    public async Task Leak_EncodeErrorMidInterp_AllocatedResourcesStillReleased()
    {
        // Interp ON, upscale OFF; the encoder throws on the first frame. The pair that was
        // already interpolated must still have its intermediates released and its array freed.
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 1080, TimeSpan.FromSeconds(2), 5),
            FrameCount = 5,
        };
        var (pipeline, bridge, _, _) = Build(spec);
        bridge.InterpFramesPerPair = 2;
        bridge.ThrowOnEncodeFrame = new NativeBridgeException("encode boom", -5);

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("encode boom");

        // Whatever was allocated before the failure is fully cleaned up (allocated == released).
        bridge.InterpProcessCount.Should().BeGreaterThan(0);
        bridge.ReleaseTextureCount.Should().Be(bridge.InterpProcessCount * bridge.InterpFramesPerPair,
            "intermediates allocated before the error are released in the finally block");
        bridge.FreeNativeArrayCount.Should().Be(bridge.InterpProcessCount,
            "every allocated frame array is freed even on the error path");
    }

    [Fact]
    public async Task Leak_CancellationMidInterp_AllocatedResourcesStillReleased()
    {
        // Interp ON, upscale OFF; cancellation fires mid-loop. Every pair processed before the
        // cancel must have its intermediates released and its array freed (cleanup in finally).
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 1080, TimeSpan.FromSeconds(10), 100),
            FrameCount = 100,
        };
        using var cts = new CancellationTokenSource();
        spec.OnFrameRead = i =>
        {
            if (i == 5)
            {
                cts.Cancel();
            }
        };
        var (pipeline, bridge, _, _) = Build(spec);
        bridge.InterpFramesPerPair = 2;

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Cancelled");

        // The loop ran a few pairs before cancelling; all of them are fully cleaned up.
        bridge.InterpProcessCount.Should().BeGreaterThan(0);
        bridge.InterpProcessCount.Should().BeLessThan(100, "cancellation stopped the loop early");
        bridge.ReleaseTextureCount.Should().Be(bridge.InterpProcessCount * bridge.InterpFramesPerPair,
            "intermediates from every processed pair are released despite cancellation");
        bridge.FreeNativeArrayCount.Should().Be(bridge.InterpProcessCount,
            "every frame array is freed despite cancellation");
    }

    // --- progress + ETA ----------------------------------------------------

    [Fact]
    public async Task Progress_ReportsStepPercentageAndEta()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(10), 10),
            FrameCount = 10,
        };
        var (pipeline, _, _, _) = Build(spec, progressInterval: 1);
        var progress = new CapturingProgress();

        await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), progress, CancellationToken.None);

        IReadOnlyList<PipelineProgress> reports = progress.Reports;
        reports.Should().NotBeEmpty();

        // Overall progress is monotonic and reaches 100% at the end of the mux stage.
        for (int i = 1; i < reports.Count; i++)
        {
            reports[i].OverallPct.Should().BeGreaterOrEqualTo(reports[i - 1].OverallPct - 1e-6);
        }

        reports[^1].OverallPct.Should().BeApproximately(100.0, 1e-6);
        reports[^1].CurrentStep.Should().Be(PipelineStep.Mux);

        // The active loop stage (interp here) and the mux stage are both reported.
        reports.Should().Contain(r => r.CurrentStep == PipelineStep.Interp);
        reports.Should().Contain(r => r.CurrentStep == PipelineStep.Mux);

        // An ETA is established once frames are being timed (mid-run, not at the ends).
        reports.Should().Contain(r => r.Eta.HasValue);

        reports.Should().OnlyContain(r => r.StepProgressPct >= 0 && r.StepProgressPct <= 100);
        reports.Should().OnlyContain(r => r.OverallPct >= 0 && r.OverallPct <= 100.0001);
        reports.Should().OnlyContain(r => r.EpisodeCount == 1 && r.EpisodeIndex == 0);
    }

    [Fact]
    public async Task Progress_IsThrottled_ByFrameInterval()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(135, 1920, 1080, TimeSpan.FromSeconds(10), 250),
            FrameCount = 250,
        };
        var (pipeline, _, _, _) = Build(spec, progressInterval: 100);
        var progress = new CapturingProgress();

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), progress, CancellationToken.None);

        result.Success.Should().BeTrue();
        // Far fewer reports than frames (initial + 100/200 marks + final + 2 mux), proving
        // progress is not emitted per frame.
        progress.Reports.Count.Should().BeLessThan(20);
    }

    // --- cancellation ------------------------------------------------------

    [Fact]
    public async Task Cancellation_CleansUpContexts_DeletesPartialOutput_ReportsCancelled()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            Metadata = new FrameSourceMetadata(24, 1280, 720, TimeSpan.FromSeconds(10), 1000),
            FrameCount = 1000,
        };
        using var cts = new CancellationTokenSource();
        spec.OnFrameRead = i =>
        {
            if (i == 3)
            {
                cts.Cancel();
            }
        };
        var (pipeline, bridge, muxer, _) = Build(spec, progressInterval: 1);
        string outputPath = Path.Combine(folder, "5_local.mp4");

        ProcessResult result = await pipeline.ProcessAsync(Ep(5, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Cancelled");
        result.OutputPath.Should().BeNull();

        // Every created context is destroyed even on cancellation.
        bridge.InterpCreateCount.Should().Be(1);
        bridge.InterpDestroyCount.Should().Be(1);
        bridge.UpscaleCreateCount.Should().Be(1);
        bridge.UpscaleDestroyCount.Should().Be(1);
        bridge.EncodeCreateCount.Should().Be(1);
        bridge.EncodeDestroyCount.Should().Be(1);

        // Partial output (and its temp video) are removed.
        File.Exists(outputPath).Should().BeFalse();
        Directory.GetFiles(folder, "*.video.tmp").Should().BeEmpty();

        muxer.MuxCallCount.Should().Be(0, "cancellation aborts before the mux stage");
    }

    [Fact]
    public async Task Cancellation_PreCancelledToken_ReturnsCancelledWithoutProcessing()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 5 };
        var (pipeline, bridge, muxer, _) = Build(spec);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Cancelled");
        bridge.EncodeCreateCount.Should().Be(0, "a pre-cancelled token short-circuits before any work");
        muxer.MuxCallCount.Should().Be(0);
    }

    // --- error handling ----------------------------------------------------

    [Fact]
    public async Task Error_NativeBridgeFailure_CleansUpAndReturnsError()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 5 };
        var (pipeline, bridge, muxer, _) = Build(spec);
        bridge.ThrowOnEncodeFrame = new NativeBridgeException("encode boom", -5);

        ProcessResult result = await pipeline.ProcessAsync(Ep(2, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("encode boom");

        // All three contexts were created and all three are destroyed on the error path.
        bridge.InterpDestroyCount.Should().Be(bridge.InterpCreateCount).And.Be(1);
        bridge.UpscaleDestroyCount.Should().Be(bridge.UpscaleCreateCount).And.Be(1);
        bridge.EncodeDestroyCount.Should().Be(bridge.EncodeCreateCount).And.Be(1);

        muxer.MuxCallCount.Should().Be(0);
        File.Exists(Path.Combine(folder, "2_local.mp4")).Should().BeFalse();
        Directory.GetFiles(folder, "*.video.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Error_DecoderOpenFailure_ReturnsError_NoContextsCreated()
    {
        string folder = NewTempFolder();
        string src = Path.Combine(folder, "bad.mp4");
        var spec = new FakeDecoderSpec { FrameCount = 5 };
        spec.FailOnOpenPaths.Add(src);
        var (pipeline, bridge, _, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, src), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("decode failed");
        bridge.EncodeCreateCount.Should().Be(0);
        bridge.InterpCreateCount.Should().Be(0);
        bridge.UpscaleCreateCount.Should().Be(0);
    }

    [Fact]
    public async Task Error_MuxFailure_CleansUpAndReturnsError()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 3 };
        var (pipeline, bridge, muxer, _) = Build(spec);
        muxer.Fail = true;

        ProcessResult result = await pipeline.ProcessAsync(Ep(4, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("mux failed");
        bridge.EncodeDestroyCount.Should().Be(1, "contexts are destroyed even when mux fails");
        File.Exists(Path.Combine(folder, "4_local.mp4")).Should().BeFalse();
    }

    [Fact]
    public async Task Error_PerFrameTimeout_AbortsAndCleansUp()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec
        {
            // No interp/upscale so the very first frame is encoded (and delayed) directly.
            Metadata = new FrameSourceMetadata(135, 1920, 1080, TimeSpan.FromSeconds(2), 3),
            FrameCount = 3,
        };
        var (pipeline, bridge, _, _) = Build(spec, frameTimeout: TimeSpan.FromMilliseconds(1));
        bridge.EncodeFrameDelayMs = 30;

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("budget");
        bridge.EncodeDestroyCount.Should().Be(1);
    }

    [Fact]
    public async Task Error_InvalidConfig_ReturnsErrorResult()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 2 };
        var (pipeline, bridge, _, _) = Build(spec);
        var badConfig = new PipelineConfig(ProcessProfile.Local, 0, 1080, 135, 20_000, "rife", "fsr4", folder);

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), badConfig, new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("width");
        bridge.EncodeCreateCount.Should().Be(0);
    }

    // --- lifecycle (always destroy) ----------------------------------------

    [Fact]
    public async Task Lifecycle_SuccessPath_DestroysEveryCreatedContext()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 4 };
        var (pipeline, bridge, _, _) = Build(spec);

        ProcessResult result = await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        bridge.InterpDestroyCount.Should().Be(bridge.InterpCreateCount);
        bridge.UpscaleDestroyCount.Should().Be(bridge.UpscaleCreateCount);
        bridge.EncodeDestroyCount.Should().Be(bridge.EncodeCreateCount);
    }

    [Fact]
    public async Task Lifecycle_DecoderDisposed_EvenOnError()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 3 };
        var bridge = new FakeNativeBridge();
        var muxer = new FakeAudioMuxer();
        FakeFrameDecoder? created = null;
        var pipeline = new ProcessingPipeline(bridge, () => created = new FakeFrameDecoder(spec), muxer);
        bridge.ThrowOnEncodeFrame = new NativeBridgeException("boom");

        await pipeline.ProcessAsync(Ep(1, Path.Combine(folder, "s.mp4")), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        created.Should().NotBeNull();
        created!.Disposed.Should().BeTrue("the decoder is disposed in the finally block");
    }

    // --- batch -------------------------------------------------------------

    [Fact]
    public async Task Batch_FailureInOneEpisode_DoesNotAbortTheRest()
    {
        string folder = NewTempFolder();
        string src1 = Path.Combine(folder, "ep1.mp4");
        string src2 = Path.Combine(folder, "ep2.mp4");
        string src3 = Path.Combine(folder, "ep3.mp4");
        var spec = new FakeDecoderSpec { FrameCount = 3 };
        spec.FailOnOpenPaths.Add(src1); // episode 1 fails to decode
        var (pipeline, bridge, muxer, _) = Build(spec);
        var progress = new CapturingProgress();
        var episodes = new List<Episode> { Ep(1, src1), Ep(2, src2), Ep(3, src3) };

        ProcessResult result = await pipeline.ProcessBatchAsync(episodes, LocalConfig(folder), progress, CancellationToken.None);

        // Aggregate reflects the failure, but episodes 2 and 3 still ran to completion.
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ep1.mp4");
        bridge.EncodeCreateCount.Should().Be(2, "episodes 2 and 3 were still processed");
        muxer.MuxCallCount.Should().Be(2);
        File.Exists(Path.Combine(folder, "2_local.mp4")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "3_local.mp4")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "1_local.mp4")).Should().BeFalse();

        // Progress carried the batch position of the last episode.
        progress.Reports.Should().Contain(r => r.EpisodeIndex == 2 && r.EpisodeCount == 3);
    }

    [Fact]
    public async Task Batch_AllSucceed_AggregatesSuccessAndSize()
    {
        string folder = NewTempFolder();
        var spec = new FakeDecoderSpec { FrameCount = 3 };
        var (pipeline, bridge, muxer, _) = Build(spec);
        var episodes = new List<Episode>
        {
            Ep(1, Path.Combine(folder, "a.mp4")),
            Ep(2, Path.Combine(folder, "b.mp4")),
        };

        ProcessResult result = await pipeline.ProcessBatchAsync(episodes, LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.OutputPath.Should().BeNull("a batch produces multiple files");
        result.OutputSizeBytes.Should().BeGreaterThan(0);
        bridge.EncodeCreateCount.Should().Be(2);
        muxer.MuxCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Batch_EmptyList_ReturnsSuccess()
    {
        string folder = NewTempFolder();
        var (pipeline, bridge, _, _) = Build();

        ProcessResult result = await pipeline.ProcessBatchAsync(new List<Episode>(), LocalConfig(folder), new CapturingProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        bridge.EncodeCreateCount.Should().Be(0);
    }

    // --- argument guards ---------------------------------------------------

    [Fact]
    public async Task ProcessAsync_NullArguments_Throw()
    {
        string folder = NewTempFolder();
        var (pipeline, _, _, _) = Build();
        var config = LocalConfig(folder);
        var episode = Ep(1, Path.Combine(folder, "s.mp4"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.ProcessAsync(null!, config, new CapturingProgress(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.ProcessAsync(episode, null!, new CapturingProgress(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.ProcessAsync(episode, config, null!, CancellationToken.None));
    }
}

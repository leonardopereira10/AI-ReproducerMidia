using System.Diagnostics;
using System.Runtime.InteropServices;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Core.Processing;

namespace CATRA.Services.Processing;

/// <summary>
/// Default <see cref="IProcessingPipeline"/> (ST-17). Orchestrates the offline GPU
/// pre-processing flow — decode → interpolation → upscale → encode → audio mux — over
/// three injectable abstractions: <see cref="INativeBridge"/> (GPU contexts),
/// <see cref="IFrameDecoder"/> (FFmpeg decode) and <see cref="IAudioMuxer"/> (audio mux).
/// </summary>
/// <remarks>
/// <para>
/// Every native context created during a run is destroyed in a <c>finally</c> block, so
/// GPU resources never leak even on error or cancellation. Expected failures
/// (<see cref="NativeBridgeException"/>, ffmpeg/IO errors, per-frame timeout,
/// cancellation) are converted into a failed <see cref="ProcessResult"/> rather than
/// thrown; the partial output is deleted.
/// </para>
/// <para>
/// Progress is weighted (Decode 10 / Interp 35 / Upscale 35 / Encode 15 / Mux 5), with
/// inactive stages removed and the remainder normalized to 100%. The ETA is derived from
/// the measured frames/second rate.
/// </para>
/// </remarks>
public sealed class ProcessingPipeline : IProcessingPipeline
{
    /// <summary>Default per-frame budget before the run is aborted as a GPU hang.</summary>
    public static readonly TimeSpan DefaultFrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default cadence (in decoded frames) for progress reports.</summary>
    public const int DefaultProgressFrameInterval = 100;

    // Stage weights (spec). Inactive stages contribute 0 and the rest is normalized.
    private const double WeightDecode = 10;
    private const double WeightInterp = 35;
    private const double WeightUpscale = 35;
    private const double WeightEncode = 15;
    private const double WeightMux = 5;

    // Native method codes passed to catra_interp_create / catra_upscale_create.
    // Mirror catra_gpu.h (CATRA_INTERP_* / CATRA_UPSCALE_*); keep in sync.
    private const int InterpMethodRife = 1;   // CATRA_INTERP_RIFE
    private const int InterpMethodFsr3Fg = 2; // CATRA_INTERP_FSR3FG
    private const int UpscaleMethodFsr1 = 1;  // CATRA_UPSCALE_FSR1
    private const int UpscaleMethodFsr4 = 2;  // CATRA_UPSCALE_FSR4

    private readonly INativeBridge _bridge;
    private readonly Func<IFrameDecoder> _decoderFactory;
    private readonly IAudioMuxer _audioMuxer;
    private readonly TimeSpan _frameTimeout;
    private readonly int _progressFrameInterval;

    /// <summary>Creates the pipeline over its three abstractions.</summary>
    /// <param name="bridge">Native GPU bridge (singleton).</param>
    /// <param name="decoderFactory">Produces a fresh decoder per episode.</param>
    /// <param name="audioMuxer">Audio multiplexer.</param>
    /// <param name="frameTimeout">Per-frame budget (default 30s); a frame exceeding it aborts the run.</param>
    /// <param name="progressFrameInterval">Report cadence in frames (default 100).</param>
    public ProcessingPipeline(
        INativeBridge bridge,
        Func<IFrameDecoder> decoderFactory,
        IAudioMuxer audioMuxer,
        TimeSpan? frameTimeout = null,
        int progressFrameInterval = DefaultProgressFrameInterval)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _decoderFactory = decoderFactory ?? throw new ArgumentNullException(nameof(decoderFactory));
        _audioMuxer = audioMuxer ?? throw new ArgumentNullException(nameof(audioMuxer));
        _frameTimeout = frameTimeout ?? DefaultFrameTimeout;
        _progressFrameInterval = progressFrameInterval < 1 ? DefaultProgressFrameInterval : progressFrameInterval;
    }

    /// <inheritdoc />
    public Task<ProcessResult> ProcessAsync(
        Episode episode,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(progress);

        return ProcessSingleAsync(episode, config, episodeIndex: 0, episodeCount: 1, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProcessResult> ProcessBatchAsync(
        List<Episode> episodes,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(progress);

        var batchTimer = Stopwatch.StartNew();
        var errors = new List<string>();
        long totalSize = 0;
        int count = episodes.Count;

        for (int i = 0; i < count; i++)
        {
            // RN: a failure in one episode must NOT abort the batch. Each episode is
            // isolated in ProcessSingleAsync (its own try/catch/finally + cleanup), so
            // we simply aggregate the outcome and move on.
            ProcessResult result = await ProcessSingleAsync(
                episodes[i], config, i, count, progress, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                totalSize += result.OutputSizeBytes;
            }
            else
            {
                errors.Add($"{episodes[i].FileName}: {result.ErrorMessage}");
            }
        }

        bool allSucceeded = errors.Count == 0;
        return new ProcessResult(
            Success: allSucceeded,
            OutputPath: null,
            OutputSizeBytes: totalSize,
            Duration: batchTimer.Elapsed,
            ErrorMessage: allSucceeded ? null : string.Join(" | ", errors));
    }

    // --- Single-episode orchestration --------------------------------------

    private async Task<ProcessResult> ProcessSingleAsync(
        Episode episode,
        PipelineConfig config,
        int episodeIndex,
        int episodeCount,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(episode.FilePath);

        var timer = Stopwatch.StartNew();
        string outputPath = string.Empty;
        string tempVideoPath = string.Empty;

        IFrameDecoder? decoder = null;
        IntPtr interpContext = IntPtr.Zero;
        IntPtr upscaleContext = IntPtr.Zero;
        IntPtr encodeContext = IntPtr.Zero;
        bool haveInterp = false;
        bool haveUpscale = false;
        bool haveEncode = false;
        bool succeeded = false;

        try
        {
            // Config validation errors surface as a failed ProcessResult (spec:
            // "Error Handling → ProcessResult com erro"), never as a thrown exception.
            ValidateConfig(config);

            // Honor a pre-cancelled token before doing any work.
            cancellationToken.ThrowIfCancellationRequested();

            outputPath = BuildOutputPath(episode, config);
            tempVideoPath = outputPath + ".video.tmp";

            // 1. Open source → metadata (fps, resolution, duration, total frames).
            decoder = _decoderFactory();
            decoder.Open(episode.FilePath);
            FrameSourceMetadata meta = decoder.Metadata;

            // 1b. Initialize the native bridge on the decoder's D3D11 device so
            //     the GPU backends (upscale, interp, encode) can consume the
            //     decoder's D3D11 textures. Must happen before any Create* call.
            if (decoder.D3D11DevicePtr != IntPtr.Zero && !_bridge.IsInitialized)
            {
                System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] Initializing bridge with device 0x{decoder.D3D11DevicePtr:X}");
                _bridge.Initialize(decoder.D3D11DevicePtr);
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] Bridge initialized successfully");
            }
            else if (decoder.D3D11DevicePtr == IntPtr.Zero)
            {
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] WARNING: decoder has no D3D11 device (software decode?)");
            }
            else if (_bridge.IsInitialized)
            {
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] Bridge already initialized, skipping");
            }

            // 2. RN-07 skip decisions.
            bool needInterp = meta.Fps < config.TargetFps;
            bool needUpscale = meta.Height < config.TargetHeight;
            Weights weights = ComputeWeights(needInterp, needUpscale);
            PipelineStep loopStep = SelectLoopStep(needInterp, needUpscale);

            // 3. Init native contexts (only the active stages).
            if (needInterp)
            {
                try
                {
                    interpContext = _bridge.CreateInterpolation(
                        meta.Width, meta.Height, meta.Fps, config.TargetFps, MapInterpMethod(config.InterpMethod));
                    haveInterp = true;
                }
                catch (NativeBridgeException ex)
                {
                    // Graceful degradation: RIFE model missing, ORT version mismatch,
                    // or GPU init failure. Skip interpolation and continue with
                    // upscale + encode only. The output will have the source fps
                    // (no frame interpolation) but still be upscaled + re-encoded.
                    Trace.WriteLine(
                        $"[ProcessingPipeline] Interpolation unavailable ({ex.Message}); " +
                        "continuing without frame interpolation.");
                    needInterp = false;
                    weights = ComputeWeights(needInterp, needUpscale);
                    loopStep = SelectLoopStep(needInterp, needUpscale);
                }
            }

            if (needUpscale)
            {
                upscaleContext = _bridge.CreateUpscaler(
                    meta.Width, meta.Height, config.TargetWidth, config.TargetHeight,
                    MapUpscaleMethod(config.UpscaleMethod));
                haveUpscale = true;
            }

            encodeContext = _bridge.CreateEncoder(
                config.TargetWidth, config.TargetHeight, config.EncodeBitrateKbps, config.TargetFps);
            haveEncode = true;
            System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] Encoder created: ctx={encodeContext}");

            Directory.CreateDirectory(config.OutputFolder);

            // 4–5. Frame loop + flush, writing the H.265 video stream to a temp file.
            System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] Opening output file: {tempVideoPath}");
            using (var output = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] Entering RunFrameLoop");
                RunFrameLoop(
                    decoder, output, episodeIndex, episodeCount, meta.TotalFrames,
                    needInterp, needUpscale, interpContext, upscaleContext, encodeContext,
                    weights, loopStep, timer, progress, cancellationToken);
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] RunFrameLoop completed");

                // 5. Flush encoder → remaining NALs.
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] Flushing encoder");
                _bridge.FlushEncoder(encodeContext, out IntPtr flushBuffer, out int flushSize);
                if (flushSize > 0 && flushBuffer != IntPtr.Zero)
                {
                    WriteBytes(output, flushBuffer, flushSize);
                }
            }

            ReportLoopProgress(progress, episodeIndex, episodeCount, meta.TotalFrames, meta.TotalFrames,
                weights, loopStep, timer.Elapsed);

            // 6. Mux audio (2-pass): combine source audio with the encoded video.
            ReportMuxProgress(progress, episodeIndex, episodeCount, weights, 0, timer.Elapsed);
            _audioMuxer.Mux(episode.FilePath, tempVideoPath, outputPath);
            ReportMuxProgress(progress, episodeIndex, episodeCount, weights, 100, timer.Elapsed);

            TryDelete(tempVideoPath);

            long size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            succeeded = true;
            return new ProcessResult(true, outputPath, size, timer.Elapsed, null);
        }
        catch (OperationCanceledException)
        {
            // Cancellation: discard the partial output (cleanup happens in finally).
            return new ProcessResult(false, null, 0, timer.Elapsed, "Cancelled");
        }
        catch (Exception ex)
        {
            // NativeBridgeException / ffmpeg / IO / timeout → abort with a failed result.
            System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return new ProcessResult(false, null, 0, timer.Elapsed, ex.Message);
        }
        finally
        {
            // 7. Cleanup: destroy EVERY created context, always (even on error/cancel).
            if (haveInterp)
            {
                _bridge.DestroyInterpolation(interpContext);
            }

            if (haveUpscale)
            {
                _bridge.DestroyUpscaler(upscaleContext);
            }

            if (haveEncode)
            {
                _bridge.DestroyEncoder(encodeContext);
            }

            decoder?.Dispose();

            if (!succeeded)
            {
                // Remove any partial artefacts so a failed/cancelled run leaves nothing behind.
                TryDelete(tempVideoPath);
                TryDelete(outputPath);
            }
        }
    }

    private void RunFrameLoop(
        IFrameDecoder decoder,
        FileStream output,
        int episodeIndex,
        int episodeCount,
        long totalFrames,
        bool needInterp,
        bool needUpscale,
        IntPtr interpContext,
        IntPtr upscaleContext,
        IntPtr encodeContext,
        Weights weights,
        PipelineStep loopStep,
        Stopwatch timer,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        long frameIndex = 0;
        IntPtr previous = IntPtr.Zero;
        var frameTimer = new Stopwatch();

        System.Diagnostics.Trace.WriteLine($"[RunFrameLoop] START: needInterp={needInterp}, needUpscale={needUpscale}, totalFrames={totalFrames}");
        ReportLoopProgress(progress, episodeIndex, episodeCount, totalFrames, 0, weights, loopStep, timer.Elapsed);

        try
        {
            System.Diagnostics.Trace.WriteLine("[RunFrameLoop] Calling decoder.TryReadFrame");
            while (true)
            {
                Console.Error.WriteLine($"[RunFrameLoop] TryReadFrame call #{frameIndex}...");
                Console.Error.Flush();
                bool hasFrame = decoder.TryReadFrame(out IntPtr texture);
                Console.Error.WriteLine($"[RunFrameLoop] TryReadFrame #{frameIndex} returned {hasFrame}, texture=0x{texture:X}");
                Console.Error.Flush();
                if (!hasFrame) break;
                if (frameIndex == 0)
                {
                    System.Diagnostics.Trace.WriteLine($"[RunFrameLoop] First frame read: texture=0x{texture:X}");
                }
                cancellationToken.ThrowIfCancellationRequested();
                frameTimer.Restart();

                if (needInterp)
                {
                    // Interpolation needs an A/B pair: encode the previous source frame
                    // plus the intermediates generated between it and the current frame.
                    if (previous != IntPtr.Zero)
                    {
                        if (frameIndex == 1)
                        {
                            System.Diagnostics.Trace.WriteLine($"[RunFrameLoop] InterpCall: ctx={interpContext}, prev=0x{previous:X}, cur=0x{texture:X}");
                        }
                        Console.Error.WriteLine($"[RunFrameLoop] ProcessInterpolation: frame={frameIndex}");
                        Console.Error.Flush();
                        int count = _bridge.ProcessInterpolation(interpContext, previous, texture, out IntPtr buffer);
                        Console.Error.WriteLine($"[RunFrameLoop] ProcessInterpolation done: count={count}");
                        Console.Error.Flush();
                        try
                        {
                            EncodeSingle(encodeContext, previous, needUpscale, upscaleContext, output);
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr intermediate = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                                EncodeSingle(encodeContext, intermediate, needUpscale, upscaleContext, output);
                            }
                        }
                        finally
                        {
                            // Cleanup runs on EVERY exit (success, encode error, cancellation).
                            // Each intermediate is a caller-owned AddRef'd texture (native contract);
                            // the encoder reads zero-copy and never releases. Release ALL of them here
                            // — even the ones not yet encoded when an error aborted the loop — so none
                            // leak, then free the pointer array with the MATCHING native deallocator.
                            // The array is native `new[]` (CRT heap): Marshal.FreeHGlobal would free it
                            // from the CoTaskMem heap, a cross-heap free (undefined behaviour).
                            if (buffer != IntPtr.Zero)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    IntPtr intermediate = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                                    if (intermediate != IntPtr.Zero)
                                    {
                                        _bridge.ReleaseTexture(intermediate);
                                    }
                                }

                                _bridge.FreeNativeArray(buffer);
                            }
                        }
                    }
                }
                else
                {
                    EncodeSingle(encodeContext, texture, needUpscale, upscaleContext, output);
                }

                // The previous frame is fully encoded now; release it and slide the window.
                if (previous != IntPtr.Zero)
                {
                    decoder.ReleaseFrame(previous);
                }

                previous = texture;
                frameIndex++;

                if (frameIndex % _progressFrameInterval == 0)
                {
                    ReportLoopProgress(progress, episodeIndex, episodeCount, totalFrames, frameIndex,
                        weights, loopStep, timer.Elapsed);
                }

                frameTimer.Stop();
                if (frameTimer.Elapsed > _frameTimeout)
                {
                    throw new TimeoutException(
                        $"Frame {frameIndex} exceeded the {_frameTimeout.TotalSeconds:0}s budget; aborting (GPU hang).");
                }
            }

            // In the interpolation path the final source frame never became the
            // "previous" of an A/B pair, so it has not been encoded yet — encode it
            // now. In the direct (no-interp) path every source frame was already
            // encoded inside the loop, so encoding it again would duplicate the last
            // frame; there we only release the held frame.
            if (previous != IntPtr.Zero)
            {
                if (needInterp)
                {
                    EncodeSingle(encodeContext, previous, needUpscale, upscaleContext, output);
                }

                decoder.ReleaseFrame(previous);
                previous = IntPtr.Zero;
            }
        }
        finally
        {
            // Never leak the currently-held decoded frame, whatever the exit path.
            if (previous != IntPtr.Zero)
            {
                decoder.ReleaseFrame(previous);
            }
        }
    }

    /// <summary>Upscales (when active) then encodes one texture, writing any NAL bytes out.</summary>
    private void EncodeSingle(IntPtr encodeContext, IntPtr texture, bool needUpscale, IntPtr upscaleContext, FileStream output)
    {
        // When upscaling, ProcessUpscale hands back a CALLER-OWNED texture (native Detach);
        // the encoder reads it zero-copy and never releases it, so the pipeline must — in a
        // finally, so it is freed even when the encode throws. The no-upscale path encodes
        // the decoder-owned source frame directly; that frame is owned/released by the
        // FrameDecoder (ReleaseFrame), so it must NOT be released here.
        System.Diagnostics.Trace.WriteLine($"[EncodeSingle] needUpscale={needUpscale}, texture=0x{texture:X}");
        IntPtr toEncode = needUpscale ? _bridge.ProcessUpscale(upscaleContext, texture) : texture;
        System.Diagnostics.Trace.WriteLine($"[EncodeSingle] toEncode=0x{toEncode:X}");
        try
        {
            _bridge.EncodeFrame(encodeContext, toEncode, out IntPtr packetBuffer, out int packetSize);
            System.Diagnostics.Trace.WriteLine($"[EncodeSingle] EncodeFrame returned: packetSize={packetSize}");
            if (packetSize > 0 && packetBuffer != IntPtr.Zero)
            {
                WriteBytes(output, packetBuffer, packetSize);
            }
        }
        finally
        {
            if (needUpscale && toEncode != IntPtr.Zero)
            {
                _bridge.ReleaseTexture(toEncode);
            }
        }
    }

    /// <summary>Copies a native buffer into the output stream.</summary>
    private static void WriteBytes(FileStream output, IntPtr buffer, int size)
    {
        byte[] managed = new byte[size];
        Marshal.Copy(buffer, managed, 0, size);
        output.Write(managed, 0, size);
    }

    // --- Progress ----------------------------------------------------------

    private void ReportLoopProgress(
        IProgress<PipelineProgress> progress,
        int episodeIndex,
        int episodeCount,
        long totalFrames,
        long frameIndex,
        Weights weights,
        PipelineStep step,
        TimeSpan elapsed)
    {
        double fraction = totalFrames > 0
            ? Math.Clamp((double)frameIndex / totalFrames, 0.0, 1.0)
            : 0.0;
        double stepPct = fraction * 100.0;
        double overallPct = weights.Loop / weights.Total * 100.0 * fraction;
        TimeSpan? eta = ComputeEta(totalFrames, frameIndex, elapsed);

        progress.Report(new PipelineProgress(episodeIndex, episodeCount, step, stepPct, overallPct, elapsed, eta));
    }

    private void ReportMuxProgress(
        IProgress<PipelineProgress> progress,
        int episodeIndex,
        int episodeCount,
        Weights weights,
        double muxPct,
        TimeSpan elapsed)
    {
        double clamped = Math.Clamp(muxPct, 0.0, 100.0);
        double overallPct = (weights.Loop + weights.Mux * (clamped / 100.0)) / weights.Total * 100.0;
        progress.Report(new PipelineProgress(episodeIndex, episodeCount, PipelineStep.Mux, clamped, overallPct, elapsed, null));
    }

    private static TimeSpan? ComputeEta(long totalFrames, long frameIndex, TimeSpan elapsed)
    {
        if (totalFrames <= 0 || frameIndex <= 0 || frameIndex >= totalFrames || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        double framesPerSecond = frameIndex / elapsed.TotalSeconds;
        if (framesPerSecond <= 0)
        {
            return null;
        }

        double remainingFrames = totalFrames - frameIndex;
        return TimeSpan.FromSeconds(remainingFrames / framesPerSecond);
    }

    // --- Decisions / mapping -----------------------------------------------

    private readonly record struct Weights(double Loop, double Mux, double Total);

    private static Weights ComputeWeights(bool needInterp, bool needUpscale)
    {
        double loop = WeightDecode
            + (needInterp ? WeightInterp : 0)
            + (needUpscale ? WeightUpscale : 0)
            + WeightEncode;
        double total = loop + WeightMux;
        return new Weights(loop, WeightMux, total);
    }

    private static PipelineStep SelectLoopStep(bool needInterp, bool needUpscale)
    {
        if (needInterp)
        {
            return PipelineStep.Interp;
        }

        if (needUpscale)
        {
            return PipelineStep.Upscale;
        }

        return PipelineStep.Encode;
    }

    private static int MapInterpMethod(string method) => method.ToLowerInvariant() switch
    {
        "fsr3fg" => InterpMethodFsr3Fg,
        _ => InterpMethodRife, // "rife" (default)
    };

    private static int MapUpscaleMethod(string method) => method.ToLowerInvariant() switch
    {
        "fsr1" => UpscaleMethodFsr1,
        _ => UpscaleMethodFsr4, // "fsr4" (default)
    };

    private static string BuildOutputPath(Episode episode, PipelineConfig config)
    {
        string profile = config.Profile == ProcessProfile.Dlna ? "dlna" : "local";
        return Path.Combine(config.OutputFolder, $"{episode.Id}_{profile}.mp4");
    }

    private static void ValidateConfig(PipelineConfig config)
    {
        if (config.TargetWidth <= 0)
        {
            throw new ArgumentException("Target width must be positive.", nameof(config));
        }

        if (config.TargetHeight <= 0)
        {
            throw new ArgumentException("Target height must be positive.", nameof(config));
        }

        if (config.TargetFps <= 0)
        {
            throw new ArgumentException("Target FPS must be positive.", nameof(config));
        }

        if (config.EncodeBitrateKbps <= 0)
        {
            throw new ArgumentException("Encode bitrate must be positive.", nameof(config));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(config.OutputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.InterpMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.UpscaleMethod);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked partial file is logged by the caller's result.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}

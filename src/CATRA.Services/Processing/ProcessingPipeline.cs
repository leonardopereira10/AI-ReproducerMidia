using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
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

    private readonly Func<INativeBridge> _bridgeFactory;
    private readonly Func<IFrameDecoder> _decoderFactory;
    private readonly IAudioMuxer _audioMuxer;
    private readonly TimeSpan _frameTimeout;
    private readonly int _progressFrameInterval;

    /// <summary>Creates the pipeline over its three abstractions.</summary>
    /// <param name="bridgeFactory">
    /// Produces a fresh <see cref="INativeBridge"/> per video. Each concurrent job
    /// gets its own bridge instance so GPU contexts (interp, upscale, encode) are
    /// independent — no cross-device conflicts when multiple videos process in
    /// parallel.
    /// </param>
    /// <param name="decoderFactory">Produces a fresh decoder per episode.</param>
    /// <param name="audioMuxer">Audio multiplexer.</param>
    /// <param name="frameTimeout">Per-frame budget (default 30s); a frame exceeding it aborts the run.</param>
    /// <param name="progressFrameInterval">Report cadence in frames (default 100).</param>
    public ProcessingPipeline(
        Func<INativeBridge> bridgeFactory,
        Func<IFrameDecoder> decoderFactory,
        IAudioMuxer audioMuxer,
        TimeSpan? frameTimeout = null,
        int progressFrameInterval = DefaultProgressFrameInterval)
    {
        _bridgeFactory = bridgeFactory ?? throw new ArgumentNullException(nameof(bridgeFactory));
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

        // Each video gets its own independent NativeBridge instance so concurrent
        // jobs never share GPU contexts. This is the key enabler for parallel
        // video processing (MaxParallelJobs > 1): each bridge owns its own
        // D3D11/D3D12 device binding, interp/upscale/encode contexts, and NV12
        // converter state — no cross-device conflicts.
        INativeBridge? bridge = null;
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

            // 1b. Create a per-video native bridge and bind it to THIS decoder's
            //     D3D11 device. Each concurrent job has its own bridge, so there
            //     is no device-sharing conflict between videos.
            bridge = _bridgeFactory();
            if (decoder.D3D11DevicePtr != IntPtr.Zero)
            {
                System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] Per-video bridge bound to device 0x{decoder.D3D11DevicePtr:X} (episode {episode.Id})");
                bridge.Initialize(decoder.D3D11DevicePtr);
            }
            else
            {
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] WARNING: decoder has no D3D11 device (software decode?)");
            }

            // 2. RN-07 skip decisions.
            // Only interpolate when the integer ratio is >= 2 (e.g. 24→48 = 2x, 24→60 = 2x).
            // A fractional ratio like 24→30 (1.25x, floor=1) has no integer multiple,
            // so interpolation would allocate GPU context for nothing.
            bool needInterp = meta.Fps > 0 && Math.Floor(config.TargetFps / meta.Fps) > 1;
            bool needUpscale = meta.Height < config.TargetHeight;
            Weights weights = ComputeWeights(needInterp, needUpscale);
            PipelineStep loopStep = SelectLoopStep(needInterp, needUpscale);

            // 3. Init native contexts (only the active stages).
            if (needInterp)
            {
                try
                {
                    interpContext = bridge.CreateInterpolation(
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
                upscaleContext = CreateUpscalerWithFallback(bridge, meta.Width, meta.Height, config);
                haveUpscale = true;
            }

            // ST-30: Calculate effective fps for encoder (floor-based).
            // With floor mode, the actual output fps is srcFps * floor(targetFps/srcFps).
            // Example: src=25fps, target=60Hz -> floor(2.4)=2 -> effective=50fps.
            //
            // bugfix_06: this SAME rate is handed to the audio mux, so the muxed video
            // timestamps match the encoded frame rate exactly. When interpolation is
            // skipped the frames arrive at the SOURCE fps (not TargetFps), so that is
            // the effective rate — using TargetFps there would stretch/shrink the video
            // track against the audio by TargetFps/srcFps.
            double effectiveFps;
            if (needInterp && meta.Fps > 0)
            {
                double ratio = config.TargetFps / meta.Fps;
                int intRatio = (int)Math.Floor(ratio);
                effectiveFps = intRatio > 0 ? meta.Fps * intRatio : config.TargetFps;
            }
            else if (meta.Fps > 0)
            {
                effectiveFps = meta.Fps; // pass-through: frames keep the source rate
            }
            else
            {
                effectiveFps = config.TargetFps; // unknown source fps: best available guess
            }

            encodeContext = bridge.CreateEncoder(
                config.TargetWidth, config.TargetHeight, config.EncodeBitrateKbps, effectiveFps);
            haveEncode = true;
            System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] Encoder created: ctx={encodeContext}");

            Directory.CreateDirectory(config.OutputFolder);

            // 4–5. Frame loop + flush, writing the H.265 video stream to a temp file.
            System.Diagnostics.Trace.WriteLine($"[ProcessingPipeline] Opening output file: {tempVideoPath}");
            // 16 MB FileStream buffer — reduces OS-level write frequency dramatically vs the 4 KB default.
            const int FileStreamBufferSize = 16 * 1024 * 1024;
            using (var output = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileStreamBufferSize))
            {
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] Entering RunFrameLoopAsync");

                // Buffered writer: accumulates encoded packets in RAM and flushes to disk
                // in large batches (32 MB), minimizing SSD wear from frequent small writes.
                using var writer = new BufferedPacketWriter(output, flushThresholdBytes: 32 * 1024 * 1024);

                await RunFrameLoopAsync(
                    bridge, decoder, writer, episodeIndex, episodeCount, meta.TotalFrames,
                    needInterp, needUpscale, interpContext, upscaleContext, encodeContext,
                    weights, loopStep, timer, progress, cancellationToken).ConfigureAwait(false);
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] RunFrameLoopAsync completed");

                // 5. Flush encoder → remaining NALs.
                System.Diagnostics.Trace.WriteLine("[ProcessingPipeline] Flushing encoder");
                bridge.FlushEncoder(encodeContext, out IntPtr flushBuffer, out int flushSize);
                if (flushSize > 0 && flushBuffer != IntPtr.Zero)
                {
                    writer.WriteFromNative(flushBuffer, flushSize);
                }

                // Final flush: push any remaining buffered data to disk.
                await writer.FlushAsync().ConfigureAwait(false);
            }

            ReportLoopProgress(progress, episodeIndex, episodeCount, meta.TotalFrames, meta.TotalFrames,
                weights, loopStep, timer.Elapsed);

            // 6. Mux audio (2-pass): combine source audio with the encoded video.
            //    bugfix_06: pass the exact encode fps — the raw H.265 stream carries no
            //    container timestamps, and the mux must not guess the rate (A/V drift).
            ReportMuxProgress(progress, episodeIndex, episodeCount, weights, 0, timer.Elapsed);
            _audioMuxer.Mux(episode.FilePath, tempVideoPath, outputPath, effectiveFps);
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
                bridge?.DestroyInterpolation(interpContext);
            }

            if (haveUpscale)
            {
                bridge?.DestroyUpscaler(upscaleContext);
            }

            if (haveEncode)
            {
                bridge?.DestroyEncoder(encodeContext);
            }

            decoder?.Dispose();

            // Dispose the per-video bridge (releases native GPU resources).
            if (bridge is IDisposable disposableBridge)
            {
                disposableBridge.Dispose();
            }

            if (!succeeded)
            {
                // Remove any partial artefacts so a failed/cancelled run leaves nothing behind.
                TryDelete(tempVideoPath);
                TryDelete(outputPath);
            }
        }
    }

    /// <summary>
    /// Parallel pipeline: decode+interp (producer) overlaps with upscale+encode
    /// (consumer) through a bounded FIFO <see cref="Channel{EncodableFrame}"/>.
    /// Frame ordering is preserved by the channel; the GPU's separate hardware
    /// engines (VCN decode, compute upscale/interp, AMF encode) work concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The producer reads source frames, runs interpolation (when active), and
    /// enqueues every encodable frame in output order. The consumer dequeues in
    /// FIFO order, upscales (when active), encodes, and releases the frame.
    /// A bounded capacity of 3 frames limits memory (~24 MB at 1080p BGRA)
    /// while keeping all hardware queues fed.
    /// </para>
    /// <para>
    /// Frame release ownership travels with each <see cref="EncodableFrame"/>:
    /// decoder-owned frames are released via <see cref="IFrameDecoder.ReleaseFrame"/>,
    /// interpolation-owned textures via <see cref="INativeBridge.ReleaseTexture"/>.
    /// The native pointer array from <c>ProcessInterpolation</c> is freed by the
    /// producer immediately after enqueue (individual textures keep their own
    /// COM refcounts).
    /// </para>
    /// </remarks>
    private async Task RunFrameLoopAsync(
        INativeBridge bridge,
        IFrameDecoder decoder,
        BufferedPacketWriter output,
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
        // 3-stage pipeline: Producer → Upscaler → Encoder
        // Each stage runs on its own thread, with bounded channels between them.
        // This allows GPU engines to overlap: while encode processes frame N,
        // upscale is already working on frame N+1, and decode on frame N+2.
        //
        // Channel capacity 64 keeps all hardware engines fed even when native
        // calls are synchronous/blocking (each call does a CPU→GPU→CPU round-trip).
        // With 64 frames in flight, the GPU has a deep queue to drain while the
        // CPU is blocked waiting on the current frame's fence.
        // At 1080p BGRA this is ~512 MB of GPU texture memory per channel —
        // trivial on 16 GB+ systems.
        var decodeToUpscale = Channel.CreateBounded<EncodableFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        var upscaleToEncode = Channel.CreateBounded<UpscaledFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        // Linked CTS: if any task fails, all are cancelled promptly.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;
        long decodedFrameCount = 0;

        System.Diagnostics.Trace.WriteLine(
            $"[RunFrameLoopAsync] START: needInterp={needInterp}, needUpscale={needUpscale}, totalFrames={totalFrames}");

        // ── Stage 1: Producer (decode + interpolation) → decodeToUpscale ────
        var producerTask = Task.Run(async () =>
        {
            IntPtr previous = IntPtr.Zero;
            var frameTimer = new Stopwatch();

            try
            {
                ReportLoopProgress(progress, episodeIndex, episodeCount, totalFrames, 0,
                    weights, loopStep, timer.Elapsed);

                while (true)
                {
                    linkedToken.ThrowIfCancellationRequested();
                    frameTimer.Restart();

                    bool hasFrame = decoder.TryReadFrame(out IntPtr texture);
                    if (!hasFrame) break;

                    if (needInterp && previous != IntPtr.Zero)
                    {
                        // Generate intermediate frames between previous and current.
                        int count = bridge.ProcessInterpolation(
                            interpContext, previous, texture, out IntPtr buffer);

                        // Extract all pointers up-front, then free the array immediately.
                        // Individual textures keep their own COM refcounts.
                        IntPtr[] intermediates = new IntPtr[count];
                        for (int i = 0; i < count; i++)
                        {
                            intermediates[i] = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                        }
                        if (buffer != IntPtr.Zero)
                        {
                            bridge.FreeNativeArray(buffer);
                        }

                        // Enqueue intermediates in order. If cancellation fires mid-enqueue,
                        // release any that were NOT yet queued (they never reached the channel
                        // and the consumer/finally cannot see them).
                        int enqueued = 0;
                        try
                        {
                            for (int i = 0; i < count; i++)
                            {
                                await decodeToUpscale.Writer.WriteAsync(
                                    new EncodableFrame(intermediates[i], IsDecoderOwned: false),
                                    linkedToken).ConfigureAwait(false);
                                enqueued++;
                            }
                        }
                        catch
                        {
                            for (int i = enqueued; i < count; i++)
                            {
                                bridge.ReleaseTexture(intermediates[i]);
                            }
                            throw;
                        }
                    }

                    // Enqueue current source frame (consumer releases via decoder after encode).
                    await decodeToUpscale.Writer.WriteAsync(
                        new EncodableFrame(texture, IsDecoderOwned: true),
                        linkedToken).ConfigureAwait(false);

                    // Previous decoder frame is in the channel; consumer will release it.
                    previous = texture;

                    long count2 = Interlocked.Increment(ref decodedFrameCount);
                    if (count2 % _progressFrameInterval == 0)
                    {
                        ReportLoopProgress(progress, episodeIndex, episodeCount, totalFrames, count2,
                            weights, loopStep, timer.Elapsed);
                    }

                    frameTimer.Stop();
                    if (frameTimer.Elapsed > _frameTimeout)
                    {
                        throw new TimeoutException(
                            $"Decode+interp of frame {count2} exceeded the {_frameTimeout.TotalSeconds:0}s budget; aborting (GPU hang).");
                    }
                }

                // All frames enqueued; signal the next stage.
                decodeToUpscale.Writer.Complete();
            }
            catch (Exception ex)
            {
                linkedCts.Cancel();
                decodeToUpscale.Writer.TryComplete(ex);
                throw;
            }
        }, linkedToken);

        // ── Stage 2: Upscaler (decodeToUpscale → upscaleToEncode) ───────────
        // ASYNC UPSCALE: Uses the native async worker (catra_upscale_submit_async +
        // catra_upscale_poll_result) to simulate real-time ingame processing. The GPU
        // processes frames on a dedicated worker thread while the CPU continues
        // submitting.
        var upscalerTask = Task.Run(async () =>
        {
            var frameTimer = new Stopwatch();
            long processedCount = 0;

            try
            {
                await foreach (var frame in decodeToUpscale.Reader.ReadAllAsync(linkedToken).ConfigureAwait(false))
                {
                    frameTimer.Restart();

                    IntPtr upscaledTexture;
                    bool isUpscaled;

                    if (needUpscale)
                    {
                        // ASYNC upscale: submit to native worker thread, then poll.
                        // Ownership of frame.Texture transfers to the native worker —
                        // it Release()s the source after processing (no AddRef).
                        int ticket = bridge.SubmitUpscaleAsync(upscaleContext, frame.Texture);

                        // Mark the frame as transferred so the decoder does NOT try
                        // to release it during cleanup (prevents double-release / AV).
                        if (frame.IsDecoderOwned)
                        {
                            decoder.TransferOwnership(frame.Texture);
                        }

                        // Poll until the native worker delivers the result.
                        IntPtr result = IntPtr.Zero;
                        while (result == IntPtr.Zero)
                        {
                            linkedToken.ThrowIfCancellationRequested();
                            result = bridge.PollUpscaleResult(upscaleContext, ticket);
                            if (result == IntPtr.Zero)
                            {
                                BusyWaitMs(0.1);
                            }
                        }

                        upscaledTexture = result;
                        isUpscaled = true;
                        // NOTE: source texture ownership transferred to native worker
                        // on submit — it released the source internally. Do NOT release here.

                        // Conditional busy-wait between submissions to prevent frame
                        // overlap when GPU queue is backed up. Uses 0.1ms (100μs) to
                        // minimize CPU competition with decoder. Tight Stopwatch loop
                        // instead of Task.Delay (which can stall ~15ms).
                        int pending = bridge.GetUpscalePendingCount(upscaleContext);
                        if (pending > 1)
                        {
                            BusyWaitMs(0.1);
                        }
                        linkedToken.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        // No upscale: pass the original texture through
                        upscaledTexture = frame.Texture;
                        isUpscaled = false;
                        // Ownership transfers to the next channel; don't release here
                    }

                    // Enqueue the upscaled frame for encoding
                    try
                    {
                        await upscaleToEncode.Writer.WriteAsync(
                            new UpscaledFrame(upscaledTexture, IsUpscaled: isUpscaled, IsDecoderOwned: frame.IsDecoderOwned),
                            linkedToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // If enqueue fails, release the texture if we own it
                        if (isUpscaled)
                        {
                            bridge.ReleaseTexture(upscaledTexture);
                        }
                        else if (frame.IsDecoderOwned)
                        {
                            decoder.ReleaseFrame(upscaledTexture);
                        }
                        else
                        {
                            bridge.ReleaseTexture(upscaledTexture);
                        }
                        throw;
                    }

                    frameTimer.Stop();
                    processedCount++;

                    if (frameTimer.Elapsed > _frameTimeout)
                    {
                        throw new TimeoutException(
                            $"Upscale of frame {processedCount} exceeded the {_frameTimeout.TotalSeconds:0}s budget; aborting (GPU hang).");
                    }
                }

                upscaleToEncode.Writer.Complete();
            }
            catch
            {
                linkedCts.Cancel();
                upscaleToEncode.Writer.TryComplete();
                throw;
            }
        }, linkedToken);

        // ── Stage 3: Encoder (upscaleToEncode → output) ─────────────────────
        var encoderTask = Task.Run(async () =>
        {
            var frameTimer = new Stopwatch();
            long encodedCount = 0;

            try
            {
                await foreach (var frame in upscaleToEncode.Reader.ReadAllAsync(linkedToken).ConfigureAwait(false))
                {
                    frameTimer.Restart();

                    // Encode the frame (texture is already upscaled or passthrough).
                    bridge.EncodeFrame(encodeContext, frame.Texture, out IntPtr packetBuffer, out int packetSize);
                    if (packetSize > 0 && packetBuffer != IntPtr.Zero)
                    {
                        output.WriteFromNative(packetBuffer, packetSize);
                    }

                    // Release the texture after encoding.
                    if (frame.IsUpscaled)
                    {
                        bridge.ReleaseTexture(frame.Texture);
                    }
                    else if (frame.IsDecoderOwned)
                    {
                        decoder.ReleaseFrame(frame.Texture);
                    }
                    else
                    {
                        bridge.ReleaseTexture(frame.Texture);
                    }

                    frameTimer.Stop();
                    encodedCount++;

                    if (frameTimer.Elapsed > _frameTimeout)
                    {
                        throw new TimeoutException(
                            $"Encode of frame {encodedCount} exceeded the {_frameTimeout.TotalSeconds:0}s budget; aborting (GPU hang).");
                    }
                }
            }
            catch
            {
                if (linkedCts == null)
                {
                    return; 
                }
                linkedCts.Cancel();
                throw;
            }
        }, linkedToken);

        // ── Await all three stages; propagate first failure ─────────────────
        try
        {
            await Task.WhenAll(producerTask, upscalerTask, encoderTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // external cancellation — propagate as-is
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            // Internal cancellation (one task failed, the other was cancelled).
            // Re-throw the original fault, not the cancellation.
            if (producerTask.IsFaulted) throw producerTask.Exception!.InnerException!;
            if (upscalerTask.IsFaulted) throw upscalerTask.Exception!.InnerException!;
            if (encoderTask.IsFaulted) throw encoderTask.Exception!.InnerException!;
            throw; // both cancelled without fault — shouldn't happen
        }
        finally
        {
            // Drain any frames left in the channels after cancellation or error.
            // Frames still hold GPU resources that must be released.
            while (decodeToUpscale.Reader.TryRead(out EncodableFrame leftover1))
            {
                if (leftover1.IsDecoderOwned)
                {
                    decoder.ReleaseFrame(leftover1.Texture);
                }
                else
                {
                    bridge.ReleaseTexture(leftover1.Texture);
                }
            }

            while (upscaleToEncode.Reader.TryRead(out UpscaledFrame leftover2))
            {
                if (leftover2.IsUpscaled)
                {
                    bridge.ReleaseTexture(leftover2.Texture);
                }
                else if (leftover2.IsDecoderOwned)
                {
                    decoder.ReleaseFrame(leftover2.Texture);
                }
                else
                {
                    bridge.ReleaseTexture(leftover2.Texture);
                }
            }
        }

        ReportLoopProgress(progress, episodeIndex, episodeCount, totalFrames, totalFrames,
            weights, loopStep, timer.Elapsed);
    }

    /// <summary>
    /// A frame queued between the producer (decode+interp) and the upscaler.
    /// <see cref="IsDecoderOwned"/> determines which release method the upscaler
    /// calls after processing.
    /// </summary>
    private readonly record struct EncodableFrame(IntPtr Texture, bool IsDecoderOwned);

    /// <summary>
    /// A frame queued between the upscaler and the encoder.
    /// <see cref="IsUpscaled"/> indicates whether the texture was produced by the
    /// upscaler (caller-owned, must be released via <see cref="INativeBridge.ReleaseTexture"/>)
    /// or passed through from the decoder (ownership determined by <see cref="IsDecoderOwned"/>).
    /// </summary>
    private readonly record struct UpscaledFrame(IntPtr Texture, bool IsUpscaled, bool IsDecoderOwned);

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
        "fsr4" => UpscaleMethodFsr4,
        _ => UpscaleMethodFsr1, // "fsr1" (default — D-PO-3)
    };

    /// <summary>
    /// Creates the upscaler with an FSR 4 → FSR 1 fallback (story 03, 2ª linha de defesa).
    /// When the requested method is FSR 4 and the native create fails
    /// (<see cref="NativeBridgeException"/> — e.g. FFX DLLs missing/unloadable after the
    /// native-side downgrade already ran, or a GPU init error), retry ONCE with FSR 1 so
    /// the export still completes. The 1ª linha is the native downgrade inside
    /// <c>catra_upscale_create</c> itself; this C# retry only fires when that still
    /// surfaces as an error. FSR 1 requests never fall back (nothing below them) and a
    /// failed FSR 1 retry propagates as usual (failed <see cref="ProcessResult"/>).
    /// </summary>
    private IntPtr CreateUpscalerWithFallback(INativeBridge bridge, int srcWidth, int srcHeight, PipelineConfig config)
    {
        int method = MapUpscaleMethod(config.UpscaleMethod);
        if (method != UpscaleMethodFsr4)
        {
            return bridge.CreateUpscaler(srcWidth, srcHeight, config.TargetWidth, config.TargetHeight, method);
        }

        try
        {
            return bridge.CreateUpscaler(srcWidth, srcHeight, config.TargetWidth, config.TargetHeight, UpscaleMethodFsr4);
        }
        catch (NativeBridgeException ex)
        {
            Trace.WriteLine(
                $"[ProcessingPipeline] FSR 4 upscaler create failed ({ex.Message}); falling back to FSR 1.");
            return bridge.CreateUpscaler(srcWidth, srcHeight, config.TargetWidth, config.TargetHeight, UpscaleMethodFsr1);
        }
    }

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

    /// <summary>
    /// Accumulates encoded packet data in a reusable RAM buffer and flushes to the
    /// underlying <see cref="FileStream"/> in large batches. This avoids a disk syscall
    /// per frame — the #1 bottleneck when encode is faster than sequential I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The internal buffer is grown on demand (up to <see cref="_flushThresholdBytes"/>)
    /// and reused across frames, so there is zero per-frame allocation after warm-up.
    /// Flushes use synchronous writes to the FileStream (which has its own 16 MB buffer),
    /// so the actual OS-level writes are infrequent and large — minimizing SSD wear.
    /// </para>
    /// <para>
    /// Default threshold is 32 MB: on a 1-hour episode at 24fps with ~100 KB/frame H.265,
    /// this means ~1 flush every 5 minutes of video. Total RAM: ~32 MB buffer + 16 MB
    /// FileStream = 48 MB per episode.
    /// </para>
    /// </remarks>
    private sealed class BufferedPacketWriter : IDisposable
    {
        private readonly FileStream _output;
        private readonly int _flushThresholdBytes;
        private byte[] _buffer;
        private int _position;

        /// <param name="output">Target file stream (should have a large internal buffer).</param>
        /// <param name="flushThresholdBytes">Flush to disk when accumulated data exceeds this (default 32 MB).</param>
        public BufferedPacketWriter(FileStream output, int flushThresholdBytes = 32 * 1024 * 1024)
        {
            _output = output;
            _flushThresholdBytes = flushThresholdBytes;
            _buffer = new byte[Math.Max(flushThresholdBytes, 64 * 1024)];
        }

        /// <summary>
        /// Copies <paramref name="size"/> bytes from a native buffer into the internal
        /// accumulator. Flushes to disk when the threshold is reached.
        /// </summary>
        public void WriteFromNative(IntPtr nativeBuffer, int size)
        {
            if (size <= 0 || nativeBuffer == IntPtr.Zero) return;

            EnsureCapacity(size);
            Marshal.Copy(nativeBuffer, _buffer, _position, size);
            _position += size;

            if (_position >= _flushThresholdBytes)
            {
                FlushToStream();
            }
        }

        /// <summary>Flushes any remaining buffered data to the underlying stream.</summary>
        public async Task FlushAsync(CancellationToken ct = default)
        {
            if (_position > 0)
            {
                await _output.WriteAsync(_buffer, 0, _position, ct).ConfigureAwait(false);
                _position = 0;
            }

            await _output.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            // Synchronous fallback flush in case FlushAsync was not called.
            if (_position > 0)
            {
                _output.Write(_buffer, 0, _position);
                _position = 0;
            }
        }

        private void EnsureCapacity(int additionalBytes)
        {
            int required = _position + additionalBytes;
            if (required <= _buffer.Length) return;

            // Grow to at least 2x current or the required size, whichever is larger.
            int newSize = Math.Max(_buffer.Length * 2, required);
            Array.Resize(ref _buffer, newSize);
        }

        private void FlushToStream()
        {
            if (_position <= 0) return;
            _output.Write(_buffer, 0, _position);
            _position = 0;
        }
    }

    /// <summary>
    /// Busy-waits for the specified duration using a tight Stopwatch loop. Unlike
    /// Thread.Sleep or Task.Delay (which can deprioritize the thread and stall for
    /// ~15ms), this spins the CPU to guarantee sub-millisecond precision. Acceptable
    /// here because the GPU is the bottleneck, not the CPU.
    /// </summary>
    /// <param name="milisseconds">Duration to wait (e.g. 0.1 = 100μs).</param>
    private static void BusyWaitMs(double milisseconds = 0.1)
    {
        long targetTicks = (long)((Stopwatch.Frequency / 1000) * milisseconds);
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedTicks < targetTicks)
        {
            // Intentional CPU spin for sub-millisecond precision
        }
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

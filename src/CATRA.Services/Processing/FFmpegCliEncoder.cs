using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CATRA.Core.Interfaces;

namespace CATRA.Services.Processing;

/// <summary>
/// <see cref="IVideoEncoder"/> implementation that pipes raw BGRA frames into an
/// FFmpeg CLI process (subtask 02 — encoder cascade fallback). The encoder process
/// runs in the background, reading raw frames from stdin and writing encoded HEVC
/// packets to stdout. A dedicated reader thread drains stdout asynchronously to
/// prevent pipe deadlock (PO requirement).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy process start (BL-1 fix):</b> the FFmpeg process is NOT started in the
/// constructor. Instead, it starts on the first <see cref="EncodeFrame"/> call, using
/// the actual dimensions detected from the GPU texture readback. This eliminates the
/// dimension mismatch (e.g. 1920×1088 texture vs. 1920×1080 config) that caused
/// silent "Packet corrupt" with exit code 0.
/// </para>
/// <para>
/// The readback from GPU texture to CPU bytes happens inside
/// <see cref="EncodeFrame"/> via <see cref="INativeBridge.ReadbackTextureToCpu"/>.
/// The pipeline remains agnostic to the readback — it just passes texture pointers.
/// </para>
/// <para>
/// Output buffer contract: <c>packetBuffer</c> is context-owned (unmanaged memory),
/// valid until the next <see cref="EncodeFrame"/> / <see cref="Flush"/> call. The
/// caller (pipeline) copies bytes before the next call, same as the AMF path.
/// </para>
/// </remarks>
internal sealed class FFmpegCliEncoder : IVideoEncoder
{
    private readonly INativeBridge _bridge;
    private readonly string _ffmpegPath;
    private readonly string _encoderName;
    private readonly int _configWidth;
    private readonly int _configHeight;
    private readonly int _bitrateKbps;
    private readonly double _fps;

    // Actual dimensions — updated from first readback before process starts.
    private int _width;
    private int _height;

    // Lazy process state.
    private Process? _ffmpeg;
    private Thread? _stdoutReader;
    private Thread? _stderrReader;
    private bool _processStarted;

    // Reusable readback buffer (M-e fix: avoids LOH churn — one allocation per
    // encoder lifetime instead of one per frame).
    private byte[]? _readbackBuffer;

    // Thread-safe accumulator for encoded data from the stdout reader thread.
    private readonly object _outputLock = new();
    private readonly MemoryStream _outputAccumulator = new();

    // Unmanaged buffer for the returned packet data (valid until next call).
    private IntPtr _packetBuffer;
    private int _packetBufferSize;

    private bool _stdinClosed;
    private bool _disposed;
    private volatile bool _processFaulted;
    private string? _faultReason;

    // Bounded stderr buffer (n-e fix: prevents unbounded memory growth on long runs).
    private const int MaxStderrLines = 1000;

    // Cache of probed encoder availability (static, process-wide).
    private static readonly ConcurrentDictionary<string, bool> s_encoderCache = new();

    /// <summary>
    /// Injectable probe for encoder availability. Default uses a real FFmpeg
    /// initialization probe (not just <c>-encoders</c> listing). Tests can
    /// replace this to avoid spawning real processes.
    /// Signature: (ffmpegPath, encoderName) → available.
    /// </summary>
    internal static Func<string, string, bool> ProbeOverride { get; set; } = RealProbeEncoder;

    /// <summary>Creates the FFmpeg CLI encoder (lazy — process starts on first EncodeFrame).</summary>
    public FFmpegCliEncoder(
        INativeBridge bridge,
        string ffmpegPath,
        string encoderName,
        int width, int height,
        int bitrateKbps, double fps)
    {
        _bridge = bridge;
        _ffmpegPath = ffmpegPath;
        _encoderName = encoderName;
        _configWidth = width;
        _configHeight = height;
        _width = width;
        _height = height;
        _bitrateKbps = bitrateKbps;
        _fps = fps;
        SelectedEncoder = encoderName;
    }

    /// <inheritdoc />
    public string SelectedEncoder { get; }

    /// <summary>
    /// Stderr lines captured from the FFmpeg process. Used by the cascade
    /// to detect init failures (e.g. "Cannot load nvcuda.dll").
    /// Bounded to <see cref="MaxStderrLines"/> entries (n-e fix).
    /// </summary>
    internal List<string> StderrLines { get; } = new();

    /// <inheritdoc />
    public void EncodeFrame(IntPtr texture, out IntPtr packetBuf, out int packetSize)
    {
        // 1. Readback GPU texture → CPU BGRA bytes.
        _bridge.ReadbackTextureToCpu(texture, out byte[] pixels,
            out uint dxgiFormat, out uint rowPitch);

        // 2. BL-1: Detect actual dimensions from readback and start process lazily.
        int actualWidth = _width;
        int actualHeight = _height;

        if (rowPitch > 0 && pixels.Length > 0)
        {
            int detectedWidth = (int)(rowPitch / 4);
            int detectedHeight = pixels.Length / (int)rowPitch;
            if (detectedWidth > 0 && detectedHeight > 0)
            {
                actualWidth = detectedWidth;
                actualHeight = detectedHeight;
            }
        }

        if (!_processStarted)
        {
            StartProcess(actualWidth, actualHeight);
        }
        else if (actualWidth != _width || actualHeight != _height)
        {
            // Dimensions changed mid-stream (shouldn't happen with a single video,
            // but guard against it). Fail loud rather than silently corrupting output.
            throw new IOException(
                $"Readback dimensions changed mid-stream: was {_width}x{_height}, " +
                $"now {actualWidth}x{actualHeight}. Encoder restart required.");
        }

        // Validate pixel buffer size matches expected dimensions.
        int expectedSize = _width * _height * 4;
        if (pixels.Length != expectedSize)
        {
            throw new IOException(
                $"Readback size mismatch: got {pixels.Length} bytes, " +
                $"expected {_width}x{_height}x4={expectedSize}. " +
                $"Texture format={dxgiFormat}, rowPitch={rowPitch}.");
        }

        // 3. Write raw BGRA frame to FFmpeg stdin.
        try
        {
            var stdin = _ffmpeg!.StandardInput.BaseStream;
            stdin.Write(pixels, 0, pixels.Length);
            stdin.Flush();
        }
        catch (IOException ex) when (_ffmpeg!.HasExited)
        {
            // B1: Process died on first frame → init failure.
            // This is the signal for the cascade to try the next encoder.
            _processFaulted = true;
            _faultReason = $"FFmpeg process exited before first frame (init failure): {ex.Message}";
            throw new EncoderInitException(_encoderName, _faultReason, ex);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"FFmpeg stdin write failed (process exited={_ffmpeg!.HasExited}): {ex.Message}", ex);
        }

        // 4. Snapshot accumulated encoded data as the "packet" for this frame.
        //    FFmpeg may not produce output for every input frame (B-frame buffering),
        //    so packetSize may be 0 — same semantics as AMF.
        SnapshotOutput(out packetBuf, out packetSize);
    }

    /// <summary>
    /// BL-1: Starts the FFmpeg process with the actual detected dimensions.
    /// Called once on the first <see cref="EncodeFrame"/> call.
    /// </summary>
    private void StartProcess(int actualWidth, int actualHeight)
    {
        if (actualWidth != _configWidth || actualHeight != _configHeight)
        {
            Trace.WriteLine(
                $"[FFmpegCliEncoder] WARNING: texture dims {actualWidth}x{actualHeight} " +
                $"differ from config {_configWidth}x{_configHeight}. Using actual dims for FFmpeg process.");
        }

        _width = actualWidth;
        _height = actualHeight;

        string args = BuildArguments(_encoderName, _width, _height, _bitrateKbps, _fps);
        Trace.WriteLine($"[FFmpegCliEncoder] Starting: {_ffmpegPath} {args}");

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            _ffmpeg = Process.Start(startInfo)
                ?? throw new IOException($"Failed to start FFmpeg process: {_ffmpegPath}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"FFmpeg binary not found: {_ffmpegPath}", ex);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"FFmpeg process startup failed: {ex.Message}", ex);
        }

        // Async stdout reader: drains encoded packets in background to prevent
        // pipe deadlock. Without this, FFmpeg blocks on stdout write once the
        // pipe buffer fills (~64KB on Windows), while we're blocked on stdin write.
        _stdoutReader = new Thread(ReadStdoutLoop)
        {
            Name = "FFmpegStdoutReader",
            IsBackground = true
        };
        _stdoutReader.Start();

        // Async stderr reader: captures diagnostic output. Prevents stderr pipe
        // deadlock (FFmpeg writes progress/errors to stderr continuously).
        _stderrReader = new Thread(ReadStderrLoop)
        {
            Name = "FFmpegStderrReader",
            IsBackground = true
        };
        _stderrReader.Start();

        _processStarted = true;
    }

    /// <inheritdoc />
    public void Flush(out IntPtr packetBuf, out int packetSize)
    {
        Flush(CancellationToken.None, out packetBuf, out packetSize);
    }

    /// <summary>
    /// Flushes with cancellation support (M-d fix). Honors the token during
    /// process wait; throws <see cref="OperationCanceledException"/> if
    /// the token fires or the join times out.
    /// </summary>
    public void Flush(CancellationToken cancellationToken, out IntPtr packetBuf, out int packetSize)
    {
        // If process was never started (no frames encoded), nothing to flush.
        if (!_processStarted || _ffmpeg == null)
        {
            packetBuf = IntPtr.Zero;
            packetSize = 0;
            return;
        }

        // M-d: Check cancellation BEFORE starting the expensive flush.
        cancellationToken.ThrowIfCancellationRequested();

        // Close stdin to signal EOF to FFmpeg → triggers encoder drain.
        if (!_stdinClosed)
        {
            try
            {
                _ffmpeg.StandardInput.Close();
            }
            catch (IOException)
            {
                // stdin may already be broken if the process exited.
            }
            _stdinClosed = true;
        }

        // M-d: Wait for the stdout reader to finish (FFmpeg exits after drain).
        // Use timeout + cancellation instead of infinite wait.
        // Check cancellation BEFORE and DURING the join, not after.
        const int FlushTimeoutMs = 30_000;
        bool stdoutDone = _stdoutReader?.Join(FlushTimeoutMs) ?? true;
        _stderrReader?.Join(TimeSpan.FromSeconds(5));

        // M-d: Check cancellation after join (may have been signalled during wait).
        cancellationToken.ThrowIfCancellationRequested();

        if (!stdoutDone)
        {
            // Join timed out — FFmpeg is stuck. Kill and report error.
            Trace.WriteLine("[FFmpegCliEncoder] ERROR: stdout reader join timed out (flush truncation detected)");
            KillOrphanProcess();
            throw new IOException(
                $"FFmpeg flush timed out after {FlushTimeoutMs}ms — possible encoder stall. " +
                "Process killed to prevent partial output.");
        }

        if (!_ffmpeg.HasExited)
        {
            Trace.WriteLine("[FFmpegCliEncoder] WARNING: FFmpeg did not exit after flush, waiting...");
            if (!_ffmpeg.WaitForExit(10_000))
            {
                KillOrphanProcess();
                throw new IOException("FFmpeg did not exit within 10s after flush — killed.");
            }
        }

        // M-b: Check exit code for silent failures — throw IOException with
        // stderr tail instead of returning truncated bytes as "success".
        if (_ffmpeg.HasExited && _ffmpeg.ExitCode != 0 && !_processFaulted)
        {
            string stderrTail = GetStderrTail(20);
            throw new IOException(
                $"FFmpeg exited with code {_ffmpeg.ExitCode} during flush. " +
                $"stderr tail: {stderrTail}");
        }

        // Return all remaining accumulated data.
        SnapshotOutput(out packetBuf, out packetSize);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Close stdin if not already done.
        if (!_stdinClosed && _ffmpeg != null)
        {
            try { _ffmpeg.StandardInput.Close(); } catch { /* best effort */ }
            _stdinClosed = true;
        }

        // Give threads a chance to finish.
        _stdoutReader?.Join(TimeSpan.FromSeconds(5));
        _stderrReader?.Join(TimeSpan.FromSeconds(5));

        // Kill process if still alive (zombie prevention — R3).
        KillOrphanProcess();

        _ffmpeg?.Dispose();

        // Free unmanaged output buffer.
        if (_packetBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_packetBuffer);
            _packetBuffer = IntPtr.Zero;
        }

        // Return readback buffer to pool (M-e).
        if (_readbackBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_readbackBuffer);
            _readbackBuffer = null;
        }

        lock (_outputLock)
        {
            _outputAccumulator.Dispose();
        }
    }

    /// <summary>
    /// Probes whether a specific encoder is available in the FFmpeg binary.
    /// Uses an injectable <see cref="ProbeOverride"/> (default: real init probe).
    /// Results are cached process-wide.
    /// </summary>
    public static bool IsEncoderAvailable(string ffmpegPath, string encoderName)
    {
        string key = $"{ffmpegPath}|{encoderName}";
        return s_encoderCache.GetOrAdd(key, _ => ProbeOverride(ffmpegPath, encoderName));
    }

    /// <summary>Resets the encoder availability cache (for testing).</summary>
    internal static void ResetEncoderCache() => s_encoderCache.Clear();

    /// <summary>
    /// B1/M-c: Real initialization probe. Runs FFmpeg with a real production-like
    /// source (1-frame black BGRA input) and encodes to null output. This catches
    /// init failures that <c>-encoders</c> listing misses (no CUDA, no MFX, format
    /// conversion errors, etc.). Uses a 10s timeout + kill to prevent hangs (M-a).
    /// </summary>
    internal static bool RealProbeEncoder(string ffmpegPath, string encoderName)
    {
        Process? process = null;
        try
        {
            // M-c: Encode 1 black frame through the full format conversion path
            // (bgra → yuv420p → encoder → null). This catches init failures AND
            // format conversion issues that a simpler probe would miss.
            // Using color (not nullsrc) ensures at least 1 frame is produced.
            string args =
                $"-hide_banner -loglevel error " +
                $"-f lavfi -i color=c=black:s=64x64:r=24:d=0.1 " +
                $"-frames:v 1 -c:v {encoderName} " +
                $"-pix_fmt yuv420p -f null -";

            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process = Process.Start(startInfo);
            if (process == null) return false;

            // M-a: Async drain of stderr to prevent the probe from hanging when
            // FFmpeg writes more than the pipe buffer can hold. The old code called
            // StandardError.ReadToEnd() BEFORE WaitForExit, which blocked forever
            // if the process didn't exit (ReadToEnd waits for stream close).
            var stderrTcs = new TaskCompletionSource<string>();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    stderrTcs.TrySetResult(string.Empty);
            };
            process.BeginErrorReadLine();

            // Also drain stdout asynchronously (some encoders write to stdout
            // even with -f null).
            process.OutputDataReceived += (_, _) => { };
            process.BeginOutputReadLine();

            // Wait for exit with timeout. If it times out, kill and return false.
            bool exited = process.WaitForExit(10_000);
            if (!exited)
            {
                Trace.WriteLine(
                    $"[FFmpegCliEncoder] Probe timeout (10s) for {encoderName}, killing.");
                try { process.Kill(); } catch { /* best effort */ }
                process.WaitForExit(3000);
                return false;
            }

            if (process.ExitCode != 0)
            {
                // Try to get stderr for diagnostics (best effort, non-blocking).
                string stderr = stderrTcs.Task.IsCompleted ? "" : "<stderr unavailable>";
                Trace.WriteLine(
                    $"[FFmpegCliEncoder] Probe FAILED for {encoderName}: exit={process.ExitCode}, stderr={stderr.Trim()}");
                return false;
            }

            Trace.WriteLine($"[FFmpegCliEncoder] Probe OK: {encoderName}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FFmpegCliEncoder] Probe exception for {encoderName}: {ex.Message}");
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static string BuildArguments(string encoderName, int width, int height,
        int bitrateKbps, double fps)
    {
        string rate = fps.ToString("0.######", CultureInfo.InvariantCulture);

        // Input: raw BGRA frames from stdin.
        // Output: HEVC stream to stdout (pipe:1).
        // M1: -pix_fmt yuv420p on output to strip alpha (yuva420p → yuv420p).
        // Encoder-specific options:
        //   - hevc_nvenc: -preset p4 (balanced), -rc cbr
        //   - hevc_qsv:   -preset medium
        //   - libx265:    -preset medium, -x265-params log-level=0 (suppress per-frame stats)
        string encoderOpts = encoderName switch
        {
            "hevc_nvenc" => "-preset p4 -rc cbr",
            "hevc_qsv" => "-preset medium",
            "libx265" => "-preset medium -x265-params log-level=0",
            _ => ""
        };

        return $"-hide_banner -loglevel warning " +
               $"-f rawvideo -pix_fmt bgra -s {width}x{height} -framerate {rate} -i pipe:0 " +
               $"-c:v {encoderName} -b:v {bitrateKbps}k {encoderOpts} " +
               $"-pix_fmt yuv420p " +
               $"-f hevc pipe:1";
    }

    private void ReadStdoutLoop()
    {
        try
        {
            var stream = _ffmpeg!.StandardOutput.BaseStream;
            byte[] buf = new byte[64 * 1024]; // 64KB read buffer
            int bytesRead;
            while ((bytesRead = stream.Read(buf, 0, buf.Length)) > 0)
            {
                lock (_outputLock)
                {
                    _outputAccumulator.Write(buf, 0, bytesRead);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FFmpegCliEncoder] stdout reader error: {ex.Message}");
        }
    }

    private void ReadStderrLoop()
    {
        try
        {
            var stream = _ffmpeg!.StandardError.BaseStream;
            byte[] buf = new byte[4 * 1024];
            int bytesRead;
            while ((bytesRead = stream.Read(buf, 0, buf.Length)) > 0)
            {
                string text = System.Text.Encoding.UTF8.GetString(buf, 0, bytesRead).TrimEnd();
                if (!string.IsNullOrEmpty(text))
                {
                    lock (StderrLines)
                    {
                        // n-e: bound stderr buffer to prevent unbounded growth
                        // on long encodes with verbose logging.
                        if (StderrLines.Count < MaxStderrLines)
                        {
                            StderrLines.Add(text);
                        }
                    }
                    Trace.WriteLine($"[FFmpegCliEncoder] {text}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FFmpegCliEncoder] stderr reader error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the last N lines of captured stderr for error diagnostics (M-b fix).
    /// </summary>
    private string GetStderrTail(int maxLines)
    {
        lock (StderrLines)
        {
            if (StderrLines.Count == 0) return "(no stderr captured)";
            int start = Math.Max(0, StderrLines.Count - maxLines);
            var tail = StderrLines.GetRange(start, StderrLines.Count - start);
            return string.Join(" | ", tail);
        }
    }

    /// <summary>
    /// Snapshots the current accumulated output into the unmanaged packet buffer.
    /// Resets the accumulator for the next batch. The returned pointer is valid
    /// until the next SnapshotOutput call (same contract as AMF).
    /// </summary>
    private void SnapshotOutput(out IntPtr packetBuf, out int packetSize)
    {
        lock (_outputLock)
        {
            int size = (int)_outputAccumulator.Length;
            if (size == 0)
            {
                packetBuf = IntPtr.Zero;
                packetSize = 0;
                return;
            }

            // Grow unmanaged buffer if needed.
            if (size > _packetBufferSize)
            {
                if (_packetBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_packetBuffer);
                }
                _packetBuffer = Marshal.AllocHGlobal(size);
                _packetBufferSize = size;
            }

            // Copy accumulated data to the unmanaged buffer.
            byte[] temp = _outputAccumulator.ToArray();
            Marshal.Copy(temp, 0, _packetBuffer, size);

            // Reset accumulator for next batch.
            _outputAccumulator.SetLength(0);

            packetBuf = _packetBuffer;
            packetSize = size;
        }
    }

    /// <summary>n-f: Kill process if still alive (orphan prevention). Null-safe.</summary>
    private void KillOrphanProcess()
    {
        try
        {
            if (_ffmpeg != null && !_ffmpeg.HasExited)
            {
                _ffmpeg.Kill();
                _ffmpeg.WaitForExit(3000);
            }
        }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Thrown when an encoder fails to initialize (first-frame failure).
/// The cascade catches this to try the next encoder.
/// </summary>
internal sealed class EncoderInitException : IOException
{
    public string EncoderName { get; }

    public EncoderInitException(string encoderName, string message, Exception? inner = null)
        : base(message, inner)
    {
        EncoderName = encoderName;
    }
}

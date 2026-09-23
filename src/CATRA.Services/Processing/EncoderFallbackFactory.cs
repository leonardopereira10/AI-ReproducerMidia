using System.Diagnostics;
using CATRA.Core.Interfaces;
using CATRA.Core.Processing;

namespace CATRA.Services.Processing;

/// <summary>
/// Encoder cascade factory (subtask 02): tries encoders in priority order and
/// falls back on <see cref="NativeBridgeException"/> with
/// <c>CATRA_ERR_DEVICE</c> (-4) or <c>CATRA_ERR_NOT_IMPL</c> (-2),
/// OR on <see cref="EncoderInitException"/> (first-frame init failure).
/// </summary>
/// <remarks>
/// <para>
/// Cascade order: AMF (native, zero-copy) → hevc_nvenc (FFmpeg, NVIDIA GPU) →
/// hevc_qsv (FFmpeg, Intel GPU) → libx265 (FFmpeg, CPU). Each step emits a
/// typed <see cref="EncoderFallbackEventArgs"/> so the UI (Story 04) can
/// display a deduplicated notification.
/// </para>
/// <para>
/// Only error codes -4 (DEVICE) and -2 (NOT_IMPL) trigger fallback for AMF.
/// For FFmpeg hardware encoders, <see cref="EncoderInitException"/> (process
/// died on first frame) or <see cref="IOException"/> during construction also
/// trigger fallback. Any other error propagates as-is — the pipeline converts
/// it to a failed <see cref="ProcessResult"/> (CA-2.6).
/// </para>
/// </remarks>
internal static class EncoderFallbackFactory
{
    /// <summary>Fallback trigger error codes per PO directive.</summary>
    private const int ErrDevice = -4;    // CATRA_ERR_DEVICE
    private const int ErrNotImpl = -2;   // CATRA_ERR_NOT_IMPL

    /// <summary>Hardware encoders to try in order (NVIDIA → Intel).</summary>
    private static readonly string[] FFmpegHwEncoders = ["hevc_nvenc", "hevc_qsv"];

    /// <summary>Last-resort software encoder (always available in the vendored FFmpeg).</summary>
    private const string SoftwareEncoder = "libx265";

    /// <summary>
    /// Injectable probe for encoder availability. Default delegates to
    /// <see cref="FFmpegCliEncoder.IsEncoderAvailable"/> which does a real
    /// FFmpeg init probe (B1 fix). Tests can replace to avoid spawning processes.
    /// </summary>
    internal static Func<string, string, bool> AvailabilityProbe { get; set; }
        = FFmpegCliEncoder.IsEncoderAvailable;

    /// <summary>
    /// Injectable FFmpeg encoder constructor. Default creates a real
    /// <see cref="FFmpegCliEncoder"/>. Tests inject fakes to avoid starting
    /// real processes while still exercising the real cascade logic.
    /// </summary>
    internal static Func<INativeBridge, string, string, int, int, int, double, IVideoEncoder> FFmpegEncoderFactory { get; set; }
        = (bridge, path, enc, w, h, br, fps) => new FFmpegCliEncoder(bridge, path, enc, w, h, br, fps);

    /// <summary>
    /// Creates the best available encoder through the cascade.
    /// </summary>
    /// <param name="bridge">Native bridge for AMF and readback.</param>
    /// <param name="ffmpegPath">Path to ffmpeg.exe (vendored or PATH).</param>
    /// <param name="width">Target frame width.</param>
    /// <param name="height">Target frame height.</param>
    /// <param name="bitrateKbps">Target bitrate in kbit/s.</param>
    /// <param name="fps">Effective frame rate.</param>
    /// <param name="onFallback">Optional callback invoked on each fallback transition.</param>
    /// <returns>A working <see cref="IVideoEncoder"/>.</returns>
    /// <exception cref="NativeBridgeException">
    /// Thrown when the AMF create fails with a non-fallback error code (not -4 or -2),
    /// or when the FFmpeg process cannot start (binary not found).
    /// </exception>
    public static IVideoEncoder Create(
        INativeBridge bridge,
        string ffmpegPath,
        int width, int height,
        int bitrateKbps, double fps,
        Action<EncoderFallbackEventArgs>? onFallback = null)
    {
        string? lastFailedEncoder = null;

        // 1. Try AMF (native, zero-copy on AMD GPU).
        try
        {
            IntPtr ctx = bridge.CreateEncoder(width, height, bitrateKbps, fps);
            Trace.WriteLine($"[EncoderFallbackFactory] AMF encoder selected");
            return new AmfBridgeEncoder(bridge, ctx);
        }
        catch (NativeBridgeException ex) when (ex.ErrorCode is ErrDevice or ErrNotImpl)
        {
            lastFailedEncoder = "AMF";
            string reason = $"AMF unavailable (code={ex.ErrorCode}): {ex.Message}";
            onFallback?.Invoke(new EncoderFallbackEventArgs("AMF", "FFmpeg cascade", reason));
            Trace.WriteLine($"[EncoderFallbackFactory] {reason}");
        }

        // 2. Try FFmpeg hardware encoders (NVENC → QSV).
        foreach (string hw in FFmpegHwEncoders)
        {
            if (AvailabilityProbe(ffmpegPath, hw))
            {
                try
                {
                    var encoder = FFmpegEncoderFactory(bridge, ffmpegPath, hw,
                        width, height, bitrateKbps, fps);
                    Trace.WriteLine($"[EncoderFallbackFactory] {hw} encoder selected");
                    return encoder;
                }
                catch (EncoderInitException ex)
                {
                    // B1: Process died on first frame (e.g. "Cannot load nvcuda.dll").
                    // This IS a fallback trigger — try next encoder.
                    lastFailedEncoder = hw;
                    string reason = $"{hw} init failed: {ex.Message}";
                    onFallback?.Invoke(new EncoderFallbackEventArgs(
                        hw, "next encoder", reason));
                    Trace.WriteLine($"[EncoderFallbackFactory] {reason}");
                }
                catch (IOException ex)
                {
                    // Constructor IOException (binary not found, startup failure).
                    lastFailedEncoder = hw;
                    string reason = $"{hw} init failed: {ex.Message}";
                    onFallback?.Invoke(new EncoderFallbackEventArgs(
                        hw, "next encoder", reason));
                    Trace.WriteLine($"[EncoderFallbackFactory] {reason}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Other exceptions during init → also try next encoder.
                    // This covers the case where the process starts but immediately
                    // fails (broken pipe, MFX session error, etc.).
                    lastFailedEncoder = hw;
                    string reason = $"{hw} init failed: {ex.Message}";
                    onFallback?.Invoke(new EncoderFallbackEventArgs(
                        hw, "next encoder", reason));
                    Trace.WriteLine($"[EncoderFallbackFactory] {reason}");
                }
            }
            else
            {
                Trace.WriteLine($"[EncoderFallbackFactory] {hw} not available (probe failed)");
            }
        }

        // 3. Fallback: libx265 software encoder.
        onFallback?.Invoke(new EncoderFallbackEventArgs(
            lastFailedEncoder ?? "hardware encoders", SoftwareEncoder,
            "No hardware encoder available; using software encode"));
        Trace.WriteLine($"[EncoderFallbackFactory] Falling back to {SoftwareEncoder}");

        return FFmpegEncoderFactory(bridge, ffmpegPath, SoftwareEncoder,
            width, height, bitrateKbps, fps);
    }

    /// <summary>
    /// Resolves the FFmpeg path: prefers the vendored binary at
    /// <c>lib/ffmpeg/ffmpeg.exe</c> (same resolution as AudioMuxer), falls back
    /// to <c>ffmpeg</c> on PATH.
    /// </summary>
    public static string ResolveFFmpegPath()
    {
        string baseDir = AppContext.BaseDirectory;

        // Vendored path (same as App.xaml.cs ResolveBundledFfmpegTool).
        string vendored = Path.Combine(baseDir, "lib", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(vendored))
        {
            return vendored;
        }

        // Fallback: assume ffmpeg is on PATH.
        return "ffmpeg";
    }

    /// <summary>Resets test seams to production defaults.</summary>
    internal static void ResetTestSeams()
    {
        AvailabilityProbe = FFmpegCliEncoder.IsEncoderAvailable;
        FFmpegEncoderFactory = (bridge, path, enc, w, h, br, fps)
            => new FFmpegCliEncoder(bridge, path, enc, w, h, br, fps);
        FFmpegCliEncoder.ResetEncoderCache();
        FFmpegCliEncoder.ProbeOverride = FFmpegCliEncoder.RealProbeEncoder;
    }
}

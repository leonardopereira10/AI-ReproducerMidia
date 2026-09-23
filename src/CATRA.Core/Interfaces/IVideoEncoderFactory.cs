using CATRA.Core.Processing;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Creates an <see cref="IVideoEncoder"/> for a pipeline run (subtask 02).
/// Abstracted from the pipeline so tests can inject fake encoders without
/// starting real FFmpeg processes.
/// </summary>
public interface IVideoEncoderFactory
{
    /// <summary>
    /// Creates the best available encoder. May emit fallback events via
    /// <paramref name="onFallback"/> when the primary encoder is unavailable.
    /// </summary>
    /// <param name="bridge">Native bridge (for AMF and texture readback).</param>
    /// <param name="width">Target frame width.</param>
    /// <param name="height">Target frame height.</param>
    /// <param name="bitrateKbps">Target bitrate in kbit/s.</param>
    /// <param name="fps">Effective frame rate.</param>
    /// <param name="onFallback">Fallback event callback (may be null).</param>
    /// <returns>A working <see cref="IVideoEncoder"/>.</returns>
    IVideoEncoder Create(
        INativeBridge bridge,
        int width, int height,
        int bitrateKbps, double fps,
        Action<EncoderFallbackEventArgs>? onFallback);
}

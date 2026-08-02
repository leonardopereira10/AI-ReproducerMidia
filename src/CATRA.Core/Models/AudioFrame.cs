namespace CATRA.Core.Models;

/// <summary>
/// A buffer of decoded, resampled audio samples (interleaved 32-bit float)
/// produced by <see cref="Interfaces.IAudioDecoder"/> and consumed by
/// <see cref="Interfaces.IAudioRenderer"/> (ST-05).
/// </summary>
/// <param name="Samples">Interleaved float32 samples in the range [-1, 1].</param>
/// <param name="Count">Number of valid samples in <paramref name="Samples"/>.</param>
/// <param name="Channels">Channel count the samples are interleaved for.</param>
/// <param name="PresentationTime">Timestamp of the first sample relative to media start.</param>
public sealed record AudioFrame(
    float[] Samples,
    int Count,
    int Channels,
    TimeSpan PresentationTime)
{
    /// <summary>Number of complete interleaved frames (samples / channels).</summary>
    public int FrameCount => Channels > 0 ? Count / Channels : 0;
}

using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Decodes and resamples an audio stream into interleaved float32
/// <see cref="AudioFrame"/>s (ST-05).
/// </summary>
/// <remarks>
/// Runs its own demuxer over the same file as <see cref="IVideoDecoder"/>; the two
/// are synchronised through the playback clock (audio is the master clock). Kept
/// separate from the video decoder and behind an interface so the engine is fully
/// testable with fakes.
/// </remarks>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Negotiated output sample rate in Hz (valid after <see cref="Open"/>).</summary>
    int SampleRate { get; }

    /// <summary>Negotiated output channel count (valid after <see cref="Open"/>).</summary>
    int Channels { get; }

    /// <summary>
    /// Opens <paramref name="filePath"/> and prepares the audio decoder + resampler.
    /// </summary>
    /// <param name="filePath">Media file path.</param>
    /// <param name="audioStreamIndex">
    /// Stream index to decode, or <c>-1</c> to pick the default audio stream.
    /// </param>
    void Open(string filePath, int audioStreamIndex = -1);

    /// <summary>
    /// Reads the next buffer of resampled float32 samples, or <c>null</c> at end of stream.
    /// </summary>
    AudioFrame? ReadSamples();

    /// <summary>Seeks the audio stream to <paramref name="position"/> and flushes the decoder.</summary>
    void Seek(TimeSpan position);
}

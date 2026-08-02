namespace CATRA.Core.Interfaces;

/// <summary>
/// Plays interleaved float32 PCM through WASAPI (ST-05).
/// </summary>
/// <remarks>
/// The production implementation wraps NAudio <c>WasapiOut</c> fed by a buffered
/// wave provider (~200 ms). Abstracted so the playback engine is testable without
/// an audio device.
/// </remarks>
public interface IAudioRenderer : IDisposable
{
    /// <summary>Output volume in the range [0, 1]. Values are clamped.</summary>
    float Volume { get; set; }

    /// <summary>
    /// Playback position derived from the number of samples consumed since the last
    /// flush. Used as the master clock reference for A/V sync.
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>Prepares the output device for the given float32 format.</summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>
    /// Enqueues <paramref name="count"/> interleaved float32 samples for playback.
    /// Blocks (or drops) according to the internal buffer policy.
    /// </summary>
    void Write(float[] samples, int count);

    /// <summary>Starts or resumes audio output.</summary>
    void Play();

    /// <summary>Pauses audio output without discarding buffered samples.</summary>
    void Pause();

    /// <summary>Stops audio output.</summary>
    void Stop();

    /// <summary>Discards all buffered (not-yet-played) samples and resets <see cref="Position"/>.</summary>
    void Flush();
}

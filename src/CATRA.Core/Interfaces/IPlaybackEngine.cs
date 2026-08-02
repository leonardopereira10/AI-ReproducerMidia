using CATRA.Core.Enums;
using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Orchestrates decoding, rendering and audio for local video playback (ST-05).
/// </summary>
/// <remarks>
/// Coordinates an <see cref="IVideoDecoder"/>, <see cref="IAudioDecoder"/>,
/// <see cref="IVideoRenderer"/> and <see cref="IAudioRenderer"/> (all injected as
/// factories so the engine is unit-testable with fakes). Audio is the master clock;
/// video frames are presented against it.
/// </remarks>
public interface IPlaybackEngine : IDisposable
{
    /// <summary>Current lifecycle state.</summary>
    PlaybackState State { get; }

    /// <summary>Metadata for the currently open media, or <c>null</c> when nothing is open.</summary>
    VideoMetadata? Metadata { get; }

    /// <summary>Audio tracks available in the open media.</summary>
    IReadOnlyList<AudioTrack> AudioTracks { get; }

    /// <summary>Raised ~4 times per second with the current playback position.</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>
    /// Raised when the media reaches its natural end. Per-media resources
    /// (decoders and audio renderer) are already released when handlers run and
    /// the engine is back in <see cref="PlaybackState.Stopped"/>; calling
    /// <see cref="Play"/> afterwards requires a fresh <see cref="OpenAsync"/>.
    /// </summary>
    event EventHandler? MediaEnded;

    /// <summary>Raised on an unrecoverable playback error.</summary>
    event EventHandler<PlaybackErrorEventArgs>? Error;

    /// <summary>
    /// Opens <paramref name="filePath"/> and reads metadata. Does not start playback.
    /// </summary>
    Task<VideoMetadata> OpenAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Binds the renderer to a child window handle (from the WPF HwndHost).</summary>
    void SetOutputWindow(IntPtr windowHandle);

    /// <summary>Notifies the renderer of a new output client size.</summary>
    void ResizeOutput(int width, int height);

    /// <summary>Starts or resumes playback.</summary>
    void Play();

    /// <summary>Pauses playback.</summary>
    void Pause();

    /// <summary>Stops playback and releases per-media resources.</summary>
    void Stop();

    /// <summary>Seeks to <paramref name="position"/> (flushes decoders, audio and clock).</summary>
    void Seek(TimeSpan position);

    /// <summary>Sets output volume, clamped to [0, 1].</summary>
    void SetVolume(float volume);

    /// <summary>Returns the current playback position.</summary>
    TimeSpan GetPosition();
}

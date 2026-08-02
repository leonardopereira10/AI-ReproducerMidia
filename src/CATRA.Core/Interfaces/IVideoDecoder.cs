using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Decodes the video stream of a media file into <see cref="VideoFrame"/>s (ST-05).
/// </summary>
/// <remarks>
/// The production implementation wraps FFmpeg (D3D11VA hardware decode with a
/// software YUV→BGRA fallback). Abstracted behind this interface so the playback
/// engine can be unit-tested with a fake decoder — no FFmpeg binaries or GPU
/// required in tests.
/// </remarks>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Metadata read when the file was opened. Valid after <see cref="OpenAsync"/>.</summary>
    VideoMetadata Metadata { get; }

    /// <summary>Audio streams found in the container (for MKV multi-audio selection).</summary>
    IReadOnlyList<AudioTrack> AudioTracks { get; }

    /// <summary>Whether the decoder negotiated a hardware pixel format (D3D11VA).</summary>
    bool IsHardwareAccelerated { get; }

    /// <summary>
    /// Opens <paramref name="filePath"/>, reads stream info and creates the decoder
    /// (preferring D3D11VA, falling back to software). Populates <see cref="Metadata"/>
    /// and <see cref="AudioTracks"/>.
    /// </summary>
    Task OpenAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and decodes the next video frame, or <c>null</c> at end of stream.
    /// The caller owns the returned frame and must <see cref="VideoFrame.Dispose"/> it.
    /// </summary>
    VideoFrame? ReadVideoFrame();

    /// <summary>Seeks the video stream to <paramref name="position"/> and flushes the decoder.</summary>
    void Seek(TimeSpan position);
}

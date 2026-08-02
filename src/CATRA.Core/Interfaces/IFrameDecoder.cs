using CATRA.Core.Processing;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Sequential video-frame decoder for the offline pre-processing pipeline (ST-17).
/// Opens a source container, exposes its <see cref="FrameSourceMetadata"/> and yields
/// decoded frames one at a time as GPU texture pointers.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation (<c>CATRA.Services.Processing.FrameDecoder</c>) wraps
/// FFmpeg.AutoGen with D3D11VA hardware decode (same setup as the playback
/// <c>VideoDecoder</c>, ST-05); each returned pointer is an <c>ID3D11Texture2D*</c>.
/// Abstracted behind this interface so <see cref="IProcessingPipeline"/> can be
/// unit-tested with a fake decoder — no FFmpeg binaries or GPU required in tests.
/// </para>
/// <para>
/// Frame lifetime: a returned texture stays valid until the matching
/// <see cref="ReleaseFrame"/> call (or <see cref="IDisposable.Dispose"/>). The pipeline
/// keeps the previous frame alive while it reads the next one so interpolation can be
/// fed an A/B pair; it releases each frame once encoded.
/// </para>
/// </remarks>
public interface IFrameDecoder : IDisposable
{
    /// <summary>Metadata read when the source was opened. Valid after <see cref="Open"/>.</summary>
    FrameSourceMetadata Metadata { get; }

    /// <summary>
    /// Opens <paramref name="filePath"/>, reads stream info and creates the decoder
    /// (preferring D3D11VA, falling back to software). Populates <see cref="Metadata"/>.
    /// </summary>
    void Open(string filePath);

    /// <summary>
    /// Decodes the next frame and writes its GPU texture pointer to
    /// <paramref name="texture"/>. Returns <c>false</c> at end of stream. The caller
    /// owns the texture and must call <see cref="ReleaseFrame"/> when done with it.
    /// </summary>
    bool TryReadFrame(out IntPtr texture);

    /// <summary>
    /// Releases a texture previously returned by <see cref="TryReadFrame"/>. No-op for
    /// <see cref="IntPtr.Zero"/> or an unknown pointer.
    /// </summary>
    void ReleaseFrame(IntPtr texture);
}

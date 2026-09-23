namespace CATRA.Core.Interfaces;

/// <summary>
/// Polymorphic video encoder for the processing pipeline (subtask 02 — encoder
/// cascade). Both the native AMF path and the FFmpeg CLI fallback implement this
/// interface so the pipeline frame loop is agnostic to the underlying encoder.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EncodeFrame"/> receives a GPU texture pointer. The AMF
/// implementation passes it zero-copy to the native encoder. The FFmpeg
/// implementation performs a bridge readback (<c>ReadbackTextureToCpu</c>) and
/// pipes the raw bytes to the FFmpeg process stdin.
/// </para>
/// <para>
/// <c>packetBuffer</c> / <c>packetSize</c> follow the same ownership contract as
/// the native AMF encoder: the buffer is CONTEXT-OWNED, valid until the next
/// <see cref="EncodeFrame"/> or <see cref="Flush"/> call on the same instance.
/// The caller copies the bytes before the next call. A <c>packetSize</c> of 0 is
/// not an error — the encoder may still be buffering internally.
/// </para>
/// </remarks>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Name of the active encoder (e.g. "AMF", "hevc_nvenc", "hevc_qsv", "libx265").</summary>
    string SelectedEncoder { get; }

    /// <summary>
    /// Encodes one frame. <paramref name="texture"/> is a GPU texture pointer
    /// (<c>ID3D12Resource*</c> for AMF, any bridge-readable texture for FFmpeg).
    /// On output, <paramref name="packetBuffer"/> points to encoded data and
    /// <paramref name="packetSize"/> is its size. Both are context-owned.
    /// </summary>
    void EncodeFrame(IntPtr texture, out IntPtr packetBuffer, out int packetSize);

    /// <summary>
    /// Drains buffered packets at the end of the stream. Same buffer contract as
    /// <see cref="EncodeFrame"/>. Idempotent after the first call.
    /// </summary>
    void Flush(out IntPtr packetBuffer, out int packetSize);

    /// <summary>
    /// Drains buffered packets with cancellation support. The token is honored
    /// during the process wait (FFmpeg encoder) or ignored (AMF, which is synchronous).
    /// Default implementation delegates to the parameterless <see cref="Flush(out IntPtr, out int)"/>.
    /// </summary>
    void Flush(CancellationToken cancellationToken, out IntPtr packetBuffer, out int packetSize)
    {
        Flush(out packetBuffer, out packetSize);
    }
}

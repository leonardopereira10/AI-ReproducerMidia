namespace CATRA.Core.Interfaces;

/// <summary>
/// Multiplexes the source audio track into an encoded (video-only) output file
/// (ST-17). The pipeline runs a 2-pass flow: it first writes the H.265 video stream,
/// then calls this to combine the source audio with that video into the final MP4.
/// </summary>
/// <remarks>
/// The production implementation (<c>CATRA.Services.Processing.AudioMuxer</c>) shells
/// out to the ffmpeg CLI: stream-copy when the source audio is already AAC, otherwise
/// re-encode to AAC. Abstracted behind this interface so the pipeline is testable with
/// a fake muxer — no ffmpeg binary required in tests. Real muxing (A/V sync) is
/// validated manually.
/// </remarks>
public interface IAudioMuxer
{
    /// <summary>
    /// Muxes the audio from <paramref name="sourcePath"/> into the encoded video at
    /// <paramref name="encodedVideoPath"/>, writing the combined result to
    /// <paramref name="finalOutputPath"/>.
    /// </summary>
    /// <param name="sourcePath">Original file carrying the audio track.</param>
    /// <param name="encodedVideoPath">Encoded (video-only) stream to mux.</param>
    /// <param name="finalOutputPath">Destination of the combined file.</param>
    /// <param name="videoFps">
    /// Exact frame rate of the encoded video stream (bugfix_06). The raw H.265 stream
    /// carries no container timestamps and its embedded VUI timing is unreliable
    /// (the AMF encoder has been observed writing a VUI that parses as 135 fps for a
    /// 25 fps encode), so the muxer must be told the true rate to keep the video track
    /// in sync with the audio. Values &lt;= 0 make the implementation probe the encoded
    /// stream as a best-effort fallback.
    /// </param>
    void Mux(string sourcePath, string encodedVideoPath, string finalOutputPath, double videoFps);
}

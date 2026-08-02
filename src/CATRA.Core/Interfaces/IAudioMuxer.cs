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
    void Mux(string sourcePath, string encodedVideoPath, string finalOutputPath);
}

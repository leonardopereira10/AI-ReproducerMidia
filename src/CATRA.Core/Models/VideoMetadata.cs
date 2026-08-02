namespace CATRA.Core.Models;

/// <summary>
/// Container/stream metadata for an opened video file (ST-05).
/// Extracted by <see cref="Interfaces.IVideoDecoder"/> after opening the source.
/// </summary>
/// <param name="Duration">Total duration of the video stream.</param>
/// <param name="Fps">Source frames-per-second (average frame rate).</param>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="VideoCodec">Video codec name (e.g. <c>hevc</c>, <c>h264</c>).</param>
/// <param name="AudioCodec">Primary audio codec name, if an audio stream exists.</param>
/// <param name="Title">Container <c>title</c> tag, if present.</param>
/// <param name="IsHardwareAccelerated">Whether the decoder negotiated a hardware pixel format.</param>
public sealed record VideoMetadata(
    TimeSpan Duration,
    double Fps,
    int Width,
    int Height,
    string VideoCodec,
    string? AudioCodec,
    string? Title,
    bool IsHardwareAccelerated);

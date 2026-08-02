namespace CATRA.Core.Processing;

/// <summary>
/// Stream metadata read by <see cref="Interfaces.IFrameDecoder"/> when a source is
/// opened (ST-17). Drives the RN-07 skip decisions (interpolation / upscale) and the
/// progress ETA (total frame count).
/// </summary>
/// <param name="Fps">Source frames-per-second (average frame rate).</param>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="Duration">Total duration of the video stream.</param>
/// <param name="TotalFrames">Estimated frame count (<c>duration × fps</c>); used for ETA. May be 0 if unknown.</param>
public sealed record FrameSourceMetadata(
    double Fps,
    int Width,
    int Height,
    TimeSpan Duration,
    long TotalFrames);

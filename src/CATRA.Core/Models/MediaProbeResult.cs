namespace CATRA.Core.Models;

/// <summary>
/// Basic container metadata for a video file, extracted via
/// <see cref="Interfaces.IMediaProbeService"/>.
/// </summary>
/// <param name="DurationSec">Duration in seconds, if known.</param>
/// <param name="Fps">Source frames-per-second, if known.</param>
/// <param name="Width">Source width in pixels, if known.</param>
/// <param name="Height">Source height in pixels, if known.</param>
/// <param name="ContainerTitle">Container <c>title</c> tag (P4 fallback), if present.</param>
public sealed record MediaProbeResult(
    double? DurationSec,
    double? Fps,
    int? Width,
    int? Height,
    string? ContainerTitle);

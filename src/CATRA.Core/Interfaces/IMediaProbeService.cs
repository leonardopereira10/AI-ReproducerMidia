using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Extracts basic container metadata (duration, fps, resolution, title) from a
/// video file. Abstracted so the scanner never depends on a real ffprobe binary:
/// unit tests inject a stub, and the production implementation shells out to
/// ffprobe until ST-05 bundles FFmpeg.AutoGen.
/// </summary>
public interface IMediaProbeService
{
    /// <summary>
    /// Probes <paramref name="filePath"/>. Returns <c>null</c> when metadata
    /// cannot be read (missing binary, corrupt container, I/O error).
    /// </summary>
    Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken cancellationToken = default);
}

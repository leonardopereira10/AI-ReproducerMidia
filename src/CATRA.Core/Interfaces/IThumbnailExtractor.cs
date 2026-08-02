namespace CATRA.Core.Interfaces;

/// <summary>
/// Low-level frame grabber abstraction (RF-08, RN-05). The production
/// implementation shells out to the ffmpeg CLI; tests substitute a stub so the
/// thumbnail logic can run without the binary installed.
/// </summary>
public interface IThumbnailExtractor
{
    /// <summary>
    /// Extracts a single frame from <paramref name="inputPath"/> at
    /// <paramref name="timestampSec"/> and writes a JPEG to
    /// <paramref name="outputPath"/>. Returns <c>true</c> when the output file
    /// was produced; <c>false</c> on any failure (missing binary, decode error,
    /// timeout via <paramref name="cancellationToken"/>).
    /// </summary>
    Task<bool> ExtractFrameAsync(
        string inputPath,
        double timestampSec,
        string outputPath,
        CancellationToken cancellationToken = default);
}

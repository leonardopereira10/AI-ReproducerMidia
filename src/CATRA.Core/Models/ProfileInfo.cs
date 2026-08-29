namespace CATRA.Core.Models;

/// <summary>
/// Describes an available processing/streaming profile for an episode so a
/// consumer (e.g. HwndHost renderer or DLNA control point) can present
/// a choice and know whether the file is already processed.
/// </summary>
public sealed record ProfileInfo
{
    /// <summary>Machine-readable profile identifier
    /// (see <c>ProcessProfile</c>, persisted lowercase).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display label for the profile.</summary>
    public required string Label { get; init; }

    /// <summary>Nominal output video width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Nominal output video height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Nominal output frame rate (frames per second).</summary>
    public double Fps { get; init; }

    /// <summary>Size of the file associated with the profile in bytes, when known.</summary>
    public long? FileSizeBytes { get; init; }

    /// <summary>Whether the output for this profile already exists and can be streamed
    /// (e.g. a <c>ProcessedFile</c> row) rather than requiring a one-off process.</summary>
    public bool IsProcessed { get; init; }
}
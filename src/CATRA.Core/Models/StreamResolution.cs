namespace CATRA.Core.Models;

/// <summary>
/// Resolved streaming session for an episode: the concrete file to serve,
/// its content type, the LAN URL and the profile it was resolved against.
/// </summary>
public sealed record StreamResolution
{
    /// <summary>Absolute path of the file to stream.</summary>
    public required string FilePath { get; init; }

    /// <summary>MIME content type of the streamed file.</summary>
    public required string ContentType { get; init; }

    /// <summary>LAN URL clients use to consume the stream.</summary>
    public required string StreamUrl { get; init; }

    /// <summary>Nominal video width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Nominal video height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Nominal frame rate (frames per second).</summary>
    public double Fps { get; init; }

    /// <summary>Size of the file to stream in bytes, when known.</summary>
    public long? FileSizeBytes { get; init; }

    /// <summary>Profile identifier this resolution was produced for
    /// (see <c>ProcessProfile</c>, persisted lowercase).</summary>
    public required string Profile { get; init; }
}
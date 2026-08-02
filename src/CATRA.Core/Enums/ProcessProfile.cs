namespace CATRA.Core.Enums;

/// <summary>
/// Pre-processing output profile. Persisted as lowercase text
/// (<c>local</c> / <c>dlna</c>) in <c>ProcessedFile.Profile</c> and
/// <c>ProcessJob.Profile</c>.
/// </summary>
public enum ProcessProfile
{
    /// <summary>Local playback profile (e.g. 1080p 135fps).</summary>
    Local,

    /// <summary>DLNA streaming profile (e.g. 4K 55fps).</summary>
    Dlna,
}

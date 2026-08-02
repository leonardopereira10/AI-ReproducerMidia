namespace CATRA.Core.Enums;

/// <summary>
/// Lifecycle state of the playback engine (ST-05).
/// </summary>
/// <remarks>
/// Lives in Core (not Services) because <see cref="Interfaces.IPlaybackEngine"/> —
/// a Core contract — exposes it through <c>State</c> and <c>StateChanged</c>.
/// Core is a leaf assembly and cannot reference Services, so the enum must sit here.
/// </remarks>
public enum PlaybackState
{
    /// <summary>No media loaded, or playback fully stopped. Resources released.</summary>
    Stopped = 0,

    /// <summary>Media is actively decoding/rendering and the clock is advancing.</summary>
    Playing = 1,

    /// <summary>Playback suspended; clock frozen, decoders kept open for fast resume.</summary>
    Paused = 2,

    /// <summary>Transient state while a seek flushes and re-primes the decoders.</summary>
    Seeking = 3,
}

namespace CATRA.Core.Models;

/// <summary>
/// Lifecycle state of a DLNA casting session (ST-08, RF-06).
/// </summary>
public enum CastingState
{
    /// <summary>No active casting session.</summary>
    Idle = 0,

    /// <summary>Registering media, sending SetAVTransportURI / Play to the renderer.</summary>
    Connecting = 1,

    /// <summary>The renderer is playing the served media.</summary>
    Streaming = 2,

    /// <summary>The last casting operation failed (<see cref="CATRA.Core.Interfaces.ICastingService.ErrorMessage"/>).</summary>
    Error = 3,
}

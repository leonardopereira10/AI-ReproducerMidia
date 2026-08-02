using CATRA.Core.Models;

namespace CATRA.Services.Casting;

/// <summary>
/// Runtime bookkeeping for a discovered renderer (ST-08): the immutable
/// <see cref="DlnaDeviceInfo"/> plus the moment it was last seen during a
/// discovery pass (used for de-duplication / future presence tracking).
/// </summary>
public sealed class DlnaDevice
{
    /// <summary>Wraps <paramref name="info"/> with a fresh <see cref="LastSeen"/> stamp.</summary>
    public DlnaDevice(DlnaDeviceInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>The parsed UPnP description data.</summary>
    public DlnaDeviceInfo Info { get; }

    /// <summary>Friendly name shortcut.</summary>
    public string FriendlyName => Info.FriendlyName;

    /// <summary>Absolute AVTransport control URL shortcut.</summary>
    public string AvTransportControlUrl => Info.AvTransportControlUrl;

    /// <summary>UTC timestamp of the discovery pass that found the device.</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// SSDP discovery of DLNA MediaRenderers on the LAN (ST-08, RF-06). Sends an
/// M-SEARCH multicast, fetches each responder's UPnP description and returns
/// only devices exposing an AVTransport service.
/// </summary>
public interface IDlnaDiscoveryService
{
    /// <summary>
    /// Raised when a discovery pass completes, with the filtered device list
    /// (may be empty when no renderer answered in time).
    /// </summary>
    event EventHandler<IReadOnlyList<DlnaDeviceInfo>>? DevicesFound;

    /// <summary>
    /// Runs one discovery pass. <paramref name="timeout"/> defaults to 3s per
    /// spec; the returned list contains only AVTransport-capable renderers.
    /// </summary>
    Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

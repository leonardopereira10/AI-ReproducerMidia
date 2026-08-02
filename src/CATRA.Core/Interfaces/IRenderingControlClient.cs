using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// UPnP RenderingControl:1 SOAP volume control of a DLNA renderer (ST-08).
/// Stateless: the target device is passed per call.
/// </summary>
public interface IRenderingControlClient
{
    /// <summary>
    /// Sets <paramref name="channel"/> volume (typically <c>Master</c>),
    /// clamped to 0–100 by implementations.
    /// </summary>
    Task SetVolumeAsync(
        DlnaDeviceInfo device,
        string channel,
        int volume,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current <paramref name="channel"/> volume (0–100).</summary>
    Task<int> GetVolumeAsync(
        DlnaDeviceInfo device,
        string channel,
        CancellationToken cancellationToken = default);
}

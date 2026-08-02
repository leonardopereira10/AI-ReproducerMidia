using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// UPnP AVTransport:1 SOAP control of a DLNA renderer (ST-08, RF-06):
/// SetAVTransportURI + Play/Pause/Stop/Seek and position/transport queries.
/// Implementations are stateless — the target device is passed per call, so a
/// single instance can drive any discovered renderer.
/// </summary>
public interface IAvTransportClient
{
    /// <summary>
    /// Points the renderer at <paramref name="uri"/> with DIDL-Lite
    /// <paramref name="didlLiteMetadata"/>. Does not start playback.
    /// </summary>
    Task SetAvTransportUriAsync(
        DlnaDeviceInfo device,
        string uri,
        string didlLiteMetadata,
        CancellationToken cancellationToken = default);

    /// <summary>Starts/resumes playback (speed "1").</summary>
    Task PlayAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default);

    /// <summary>Pauses playback.</summary>
    Task PauseAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default);

    /// <summary>Stops playback.</summary>
    Task StopAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeks. <paramref name="unit"/> is typically <c>REL_TIME</c> and
    /// <paramref name="target"/> an <c>HH:MM:SS</c> timestamp.
    /// </summary>
    Task SeekAsync(
        DlnaDeviceInfo device,
        string unit,
        string target,
        CancellationToken cancellationToken = default);

    /// <summary>Queries the current position / track duration.</summary>
    Task<PositionInfo> GetPositionInfoAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the transport state (<c>PLAYING</c>, <c>PAUSED_PLAYBACK</c>,
    /// <c>STOPPED</c>, <c>TRANSITIONING</c>, ...).
    /// </summary>
    Task<string> GetTransportStateAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default);
}

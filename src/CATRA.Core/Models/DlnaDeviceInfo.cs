namespace CATRA.Core.Models;

/// <summary>
/// A DLNA MediaRenderer discovered on the LAN (ST-08, RF-06): identity plus
/// the absolute SOAP control URLs extracted from the UPnP device description.
/// </summary>
public sealed class DlnaDeviceInfo
{
    /// <summary>Human-friendly device name (e.g. "Samsung TV [Living Room]").</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Unique Device Name (UDN), e.g. <c>uuid:068e7781-...</c>.</summary>
    public string Udn { get; init; } = string.Empty;

    /// <summary>The SSDP LOCATION the description was fetched from.</summary>
    public string DescriptionUrl { get; init; } = string.Empty;

    /// <summary>Absolute AVTransport:1 control URL (always present — discovery filters on it).</summary>
    public string AvTransportControlUrl { get; init; } = string.Empty;

    /// <summary>Absolute RenderingControl:1 control URL, when advertised.</summary>
    public string? RenderingControlUrl { get; init; }

    /// <summary>Device manufacturer (e.g. "Samsung Electronics"), when advertised.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Device model name, when advertised.</summary>
    public string? ModelName { get; init; }

    /// <inheritdoc />
    public override string ToString()
        => string.IsNullOrEmpty(FriendlyName) ? Udn : FriendlyName;
}

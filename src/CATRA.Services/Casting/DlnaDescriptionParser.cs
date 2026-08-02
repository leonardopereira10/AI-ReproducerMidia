using System.Xml.Linq;
using CATRA.Core.Models;

namespace CATRA.Services.Casting;

/// <summary>
/// Parses a UPnP device description XML into <see cref="DlnaDeviceInfo"/>
/// (ST-08, RF-06). Pure/static and namespace-agnostic (matches on LocalName —
/// Samsung TVs are spec-compliant but tolerance costs nothing). Returns
/// <c>null</c> for malformed XML or devices without an AVTransport service
/// (the discovery filter).
/// </summary>
public static class DlnaDescriptionParser
{
    /// <summary>UPnP AVTransport:1 service type fragment.</summary>
    public const string AvTransportService = "AVTransport";

    /// <summary>UPnP RenderingControl:1 service type fragment.</summary>
    public const string RenderingControlService = "RenderingControl";

    /// <summary>
    /// Parses <paramref name="xml"/> (fetched from <paramref name="locationUrl"/>)
    /// and resolves relative control URLs against the description URL.
    /// </summary>
    public static DlnaDeviceInfo? Parse(string xml, string locationUrl)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }

        var device = doc.Root?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device is null)
        {
            return null;
        }

        string? avTransportUrl = null;
        string? renderingControlUrl = null;

        foreach (var service in device.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var serviceType = ChildValue(service, "serviceType") ?? string.Empty;
            var controlUrl = ChildValue(service, "controlURL");
            if (controlUrl is null)
            {
                continue;
            }

            if (serviceType.Contains(AvTransportService, StringComparison.OrdinalIgnoreCase))
            {
                avTransportUrl = ResolveUrl(locationUrl, controlUrl);
            }
            else if (serviceType.Contains(RenderingControlService, StringComparison.OrdinalIgnoreCase))
            {
                renderingControlUrl = ResolveUrl(locationUrl, controlUrl);
            }
        }

        if (avTransportUrl is null)
        {
            return null; // Filter: only AVTransport-capable renderers are cast targets.
        }

        return new DlnaDeviceInfo
        {
            FriendlyName = ChildValue(device, "friendlyName") ?? string.Empty,
            Udn = ChildValue(device, "UDN") ?? string.Empty,
            DescriptionUrl = locationUrl,
            AvTransportControlUrl = avTransportUrl,
            RenderingControlUrl = renderingControlUrl,
            Manufacturer = ChildValue(device, "manufacturer"),
            ModelName = ChildValue(device, "modelName"),
        };
    }

    /// <summary>
    /// Resolves <paramref name="controlUrl"/> against the description LOCATION:
    /// absolute URLs pass through; relative ones (e.g. <c>/upnp/control/avt</c>)
    /// are combined with the LOCATION's scheme/host/port.
    /// </summary>
    public static string ResolveUrl(string locationUrl, string controlUrl)
    {
        if (Uri.TryCreate(controlUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(locationUrl, UriKind.Absolute, out var baseUri))
        {
            return new Uri(baseUri, controlUrl).ToString();
        }

        return controlUrl;
    }

    private static string? ChildValue(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();
}

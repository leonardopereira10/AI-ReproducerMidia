using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CATRA.Core.Exceptions;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Casting;

/// <summary>
/// UPnP RenderingControl:1 SOAP client (ST-08): Master channel volume 0–100.
/// Mirrors <see cref="AvTransportClient"/> structure — pure builders/parsers
/// plus an injectable <see cref="HttpClient"/> transport.
/// </summary>
public sealed class RenderingControlClient : IRenderingControlClient
{
    /// <summary>RenderingControl:1 service type (SOAPAction / envelope namespace).</summary>
    public const string ServiceType = "urn:schemas-upnp-org:service:RenderingControl:1";

    private const string SoapEnvelopeNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string SoapEncodingStyle = "http://schemas.xmlsoap.org/soap/encoding/";

    private readonly HttpClient _httpClient;

    /// <summary>Creates the client (inject <paramref name="httpClient"/> for tests).</summary>
    public RenderingControlClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <inheritdoc />
    public Task SetVolumeAsync(
        DlnaDeviceInfo device,
        string channel,
        int volume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.RenderingControlUrl is null)
        {
            throw new DlnaControlException("O renderer não anuncia RenderingControl (controle de volume indisponível).");
        }

        var clamped = Math.Clamp(volume, 0, 100);
        var body =
            $"<u:SetVolume xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            $"<Channel>{AvTransportClient.Escape(channel)}</Channel>" +
            $"<DesiredVolume>{clamped.ToString(CultureInfo.InvariantCulture)}</DesiredVolume>" +
            "</u:SetVolume>";
        return InvokeAsync(device.RenderingControlUrl, "SetVolume", body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetVolumeAsync(
        DlnaDeviceInfo device,
        string channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.RenderingControlUrl is null)
        {
            throw new DlnaControlException("O renderer não anuncia RenderingControl (controle de volume indisponível).");
        }

        var body =
            $"<u:GetVolume xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            $"<Channel>{AvTransportClient.Escape(channel)}</Channel>" +
            "</u:GetVolume>";
        var response = await InvokeAsync(device.RenderingControlUrl, "GetVolume", body, cancellationToken)
            .ConfigureAwait(false);
        return ParseVolume(response);
    }

    // ------------------------------------------------------------------
    // Pure builders / parsers (unit-tested without HTTP)
    // ------------------------------------------------------------------

    /// <summary>Builds the SetVolume SOAP body (volume already clamped by callers).</summary>
    public static string BuildSetVolumeBody(string channel, int volume)
        => $"<u:SetVolume xmlns:u=\"{ServiceType}\">" +
           "<InstanceID>0</InstanceID>" +
           $"<Channel>{AvTransportClient.Escape(channel)}</Channel>" +
           $"<DesiredVolume>{Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture)}</DesiredVolume>" +
           "</u:SetVolume>";

    /// <summary>Parses a <c>GetVolume</c> response (<c>CurrentVolume</c>); 0 when absent/invalid.</summary>
    public static int ParseVolume(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return 0;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(responseXml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            throw new DlnaControlException("Resposta SOAP inválida do renderer DLNA (volume).");
        }

        var raw = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CurrentVolume")?.Value.Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume)
            ? Math.Clamp(volume, 0, 100)
            : 0;
    }

    private async Task<string> InvokeAsync(
        string controlUrl,
        string action,
        string actionBodyXml,
        CancellationToken cancellationToken)
    {
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            $"<s:Envelope xmlns:s=\"{SoapEnvelopeNs}\" s:encodingStyle=\"{SoapEncodingStyle}\">" +
            $"<s:Body>{actionBodyXml}</s:Body>" +
            "</s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ServiceType}#{action}\"");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DlnaControlException($"Renderer DLNA inacessível ({action}).", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DlnaControlException(
                    $"Renderer DLNA recusou {action}: HTTP {(int)response.StatusCode}. {body}");
            }

            return body;
        }
    }
}

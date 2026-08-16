using System.Security;
using System.Text;
using System.Xml.Linq;
using CATRA.Core.Exceptions;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Casting;

/// <summary>
/// UPnP AVTransport:1 SOAP client (ST-08, RF-06). HTTP POST of
/// <c>text/xml; charset="utf-8"</c> envelopes with the matching
/// <c>SOAPAction</c> header. Envelope building and response parsing are
/// pure/static so the wire format is unit-testable without a renderer;
/// transport goes through an injectable <see cref="HttpClient"/>.
/// </summary>
public sealed class AvTransportClient : IAvTransportClient
{
    /// <summary>AVTransport:1 service type (SOAPAction / envelope namespace).</summary>
    public const string ServiceType = "urn:schemas-upnp-org:service:AVTransport:1";

    private const string SoapEnvelopeNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string SoapEncodingStyle = "http://schemas.xmlsoap.org/soap/encoding/";

    private readonly HttpClient _httpClient;

    /// <summary>Creates the client (inject <paramref name="httpClient"/> for tests).</summary>
    public AvTransportClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <inheritdoc />
    public Task SetAvTransportUriAsync(
        DlnaDeviceInfo device,
        string uri,
        string didlLiteMetadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:SetAVTransportURI xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            $"<CurrentURI>{Escape(uri)}</CurrentURI>" +
            $"<CurrentURIMetaData>{Escape(didlLiteMetadata)}</CurrentURIMetaData>" +
            "</u:SetAVTransportURI>";
        return InvokeAsync(device.AvTransportControlUrl, "SetAVTransportURI", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task PlayAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:Play xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "<Speed>1</Speed>" +
            "</u:Play>";
        return InvokeAsync(device.AvTransportControlUrl, "Play", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task PauseAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:Pause xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:Pause>";
        return InvokeAsync(device.AvTransportControlUrl, "Pause", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:Stop xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:Stop>";
        return InvokeAsync(device.AvTransportControlUrl, "Stop", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task SeekAsync(
        DlnaDeviceInfo device,
        string unit,
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:Seek xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            $"<Unit>{Escape(unit)}</Unit>" +
            $"<Target>{Escape(target)}</Target>" +
            "</u:Seek>";
        return InvokeAsync(device.AvTransportControlUrl, "Seek", body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PositionInfo> GetPositionInfoAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:GetPositionInfo xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:GetPositionInfo>";
        var response = await InvokeAsync(device.AvTransportControlUrl, "GetPositionInfo", body, cancellationToken)
            .ConfigureAwait(false);
        return ParsePositionInfo(response);
    }

    /// <inheritdoc />
    public async Task<string> GetTransportStateAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:GetTransportInfo xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:GetTransportInfo>";
        var response = await InvokeAsync(device.AvTransportControlUrl, "GetTransportInfo", body, cancellationToken)
            .ConfigureAwait(false);
        return ParseTransportState(response);
    }

    // ------------------------------------------------------------------
    // Pure builders / parsers (unit-tested without HTTP)
    // ------------------------------------------------------------------

    /// <summary>Wraps an action body in the SOAP 1.1 envelope.</summary>
    public static string BuildEnvelope(string actionBodyXml)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
           $"<s:Envelope xmlns:s=\"{SoapEnvelopeNs}\" s:encodingStyle=\"{SoapEncodingStyle}\">" +
           $"<s:Body>{actionBodyXml}</s:Body>" +
           "</s:Envelope>";

    /// <summary>
    /// Constrói os metadados DIDL-Lite dinamicamente com base nos metadados reais do vídeo.
    /// </summary>
    public static string BuildDidlLiteMetadata(VideoMetadata video)
    {
        // 1. Define o MIME Type correto com base na extensão
        string mimeType = video.Extension == "mkv" ? "video/x-matroska" : "video/mp4";

        // 2. Descobre o perfil DLNA (DLNA.ORG_PN) correspondente
        string dlnaProfile = GetDlnaProfile(video.VideoCodec ?? "HEVC", video.Width, (int)video.Fps);

        // 3. Monta a string de ProtocolInfo. 
        // Se o codec for reconhecido pelo DLNA, injetamos o perfil; caso contrário, usamos o cabeçalho genérico.
        string protocolInfo = !string.IsNullOrEmpty(dlnaProfile)
            ? $"http-get:*:{mimeType}:{dlnaProfile};DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000"
            : $"http-get:*:{mimeType}:DLNA.ORG_OP=01;DLNA.ORG_CI=0";

        return "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
               "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
               "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
               "<item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
               $"<dc:title>{Escape(video.Title)}</dc:title>" +
               "<upnp:class>object.item.videoItem</upnp:class> " +
               $"<res protocolInfo=\"{protocolInfo}\">{Escape(video.Url)}</res>" +
               "</item>" +
               "</DIDL-Lite>";
    }

    /// <summary>
    /// Mapeia as propriedades técnicas do arquivo para os perfis UHD/HD oficiais do DLNA.
    /// </summary>
    private static string GetDlnaProfile(string codec, int width, int fps)
    {
        if (string.IsNullOrEmpty(codec)) return string.Empty;

        string normalizedCodec = codec.ToLower();

        // Cenários para HEVC / H.265 (Padrão para 4K moderno)
        if (normalizedCodec == "hevc" || normalizedCodec == "h265")
        {
            if (width >= 3840) // Resolução 4K Ultra HD
            {
                return fps > 30
                    ? "DLNA.ORG_PN=HEVC_MAIN_MP4_UHD_60p"
                    : "DLNA.ORG_PN=HEVC_MAIN_MP4_UHD_30p";
            }

            // HEVC em resoluções menores (como 1080p)
            return "DLNA.ORG_PN=HEVC_MAIN_MP4_HD";
        }

        // Cenários para H.264 / AVC (Padrão tradicional)
        if (normalizedCodec == "h264" || normalizedCodec == "avc")
        {
            if (width >= 3840) return "DLNA.ORG_PN=AVC_MP4_UHD";     // H.264 em 4K
            if (width >= 1920) return "DLNA.ORG_PN=AVC_MP4_EU_HD";  // H.264 em 1080p (Full HD)
            if (width >= 1280) return "DLNA.ORG_PN=AVC_MP4_HP_HD";  // H.264 em 720p (HD)

            return "DLNA.ORG_PN=AVC_MP4_MP_SD"; // Resoluções menores (SD)
        }

        // Retorna vazio se for outro formato (como VP9 ou AV1) para usar o fallback seguro de streaming
        return string.Empty;
    }

    // Método auxiliar de escape para evitar que caracteres especiais quebrem a string XML
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return System.Security.SecurityElement.Escape(text);
    }

    /// <summary>The <c>SOAPAction</c> header value for <paramref name="action"/>.</summary>
    public static string BuildSoapAction(string action) => $"\"{ServiceType}#{action}\"";

    /// <summary>Parses a <c>GetPositionInfo</c> response (tolerant of missing fields).</summary>
    public static PositionInfo ParsePositionInfo(string responseXml)
    {
        var values = ParseBodyValues(responseXml);
        values.TryGetValue("RelTime", out var relTime);
        values.TryGetValue("TrackDuration", out var trackDuration);
        return new PositionInfo(DlnaTime.Parse(relTime), DlnaTime.Parse(trackDuration));
    }

    /// <inheritdoc />
    public Task NextAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:Next xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:Next>";
        return InvokeAsync(device.AvTransportControlUrl, "Next", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task PreviousAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body =
            $"<u:Previous xmlns:u=\"{ServiceType}\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:Previous>";
        return InvokeAsync(device.AvTransportControlUrl, "Previous", body, cancellationToken);
    }

    /// <inheritdoc />
    /// <summary>Parses a <c>GetTransportInfo</c> response (<c>CurrentTransportState</c>).</summary>
    public static string ParseTransportState(string responseXml)
        => ParseBodyValues(responseXml).TryGetValue("CurrentTransportState", out var state) ? state : string.Empty;

    private static Dictionary<string, string> ParseBodyValues(string responseXml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return result;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(responseXml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            throw new DlnaControlException("Resposta SOAP inválida do renderer DLNA.");
        }

        // First element inside the SOAP Body is the action response wrapper;
        // its children are the out parameters (LocalName match: NS-agnostic).
        var body = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body");
        var wrapper = body?.Descendants().FirstOrDefault();
        if (wrapper is null)
        {
            return result;
        }

        foreach (var element in wrapper.Elements())
        {
            result[element.Name.LocalName] = element.Value.Trim();
        }

        return result;
    }

    private async Task<string> InvokeAsync(
        string controlUrl,
        string action,
        string actionBodyXml,
        CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope(actionBodyXml);

        using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        request.Headers.TryAddWithoutValidation("SOAPAction", BuildSoapAction(action));

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
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new DlnaControlException(
                    $"Renderer DLNA recusou {action}: HTTP {(int)response.StatusCode}. {ExtractFaultString(responseBody)}");
            }

            return responseBody;
        }
    }

    private static string ExtractFaultString(string soapBody)
    {
        try
        {
            var doc = XDocument.Parse(soapBody);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

namespace CATRA.Services.Casting;

/// <summary>
/// A parsed SSDP <c>HTTP/1.1 200 OK</c> M-SEARCH reply (ST-08).
/// </summary>
/// <param name="Location">The <c>LOCATION</c> header (UPnP device description URL).</param>
/// <param name="SearchTarget">The <c>ST</c> header value, when present.</param>
/// <param name="Usn">The <c>USN</c> header value, when present.</param>
public sealed record SsdpResponse(string Location, string? SearchTarget, string? Usn);

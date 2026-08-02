namespace CATRA.Services.Casting;

/// <summary>
/// Fetches a UPnP device description XML from an SSDP LOCATION (ST-08).
/// Abstracted so discovery tests can supply canned XML without HTTP.
/// </summary>
public interface IDeviceDescriptionDownloader
{
    /// <summary>GETs <paramref name="location"/>; returns the body or <c>null</c> on failure.</summary>
    Task<string?> DownloadAsync(string location, CancellationToken cancellationToken = default);
}

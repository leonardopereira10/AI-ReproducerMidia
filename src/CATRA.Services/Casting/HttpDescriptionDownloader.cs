namespace CATRA.Services.Casting;

/// <summary>
/// HTTP GET of a UPnP device description (ST-08). Short timeout: a renderer
/// that stalls on its description fetch must not hold up the whole pass.
/// </summary>
public sealed class HttpDescriptionDownloader : IDeviceDescriptionDownloader
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates the downloader (inject <paramref name="httpClient"/> for tests).</summary>
    public HttpDescriptionDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(string location, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _httpClient.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return null;
        }
    }
}

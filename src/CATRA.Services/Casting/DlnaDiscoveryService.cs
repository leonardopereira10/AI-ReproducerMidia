using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Casting;

/// <summary>
/// SSDP discovery of DLNA MediaRenderers (ST-08, RF-06): M-SEARCH multicast
/// (via <see cref="ISsdpSearcher"/>) → fetch each LOCATION description →
/// parse/filter (AVTransport required) → <see cref="DevicesFound"/>. The UI
/// re-runs a pass every 10s while the device dropdown is open.
/// </summary>
public sealed class DlnaDiscoveryService : IDlnaDiscoveryService
{
    /// <summary>SSDP search target for DLNA renderers.</summary>
    public const string MediaRendererTarget = "urn:schemas-upnp-org:device:MediaRenderer:1";

    /// <summary>Spec default collection window.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly ISsdpSearcher _searcher;
    private readonly IDeviceDescriptionDownloader _downloader;

    /// <summary>
    /// Creates the service. Dependencies default to the real raw-UDP/HTTP
    /// implementations; tests inject fakes (no multicast needed).
    /// </summary>
    public DlnaDiscoveryService(ISsdpSearcher? searcher = null, IDeviceDescriptionDownloader? downloader = null)
    {
        _searcher = searcher ?? new SsdpUdpSearcher();
        _downloader = downloader ?? new HttpDescriptionDownloader();
    }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<DlnaDeviceInfo>>? DevicesFound;

    /// <inheritdoc />
    public async Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var responses = await _searcher
            .SearchAsync(MediaRendererTarget, timeout ?? DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);

        var devices = new List<DlnaDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var locations = responses
            .Select(r => r.Location)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? xml;
            try
            {
                xml = await _downloader.DownloadAsync(location, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue; // Unreachable renderer: skip, keep the rest.
            }

            if (xml is null)
            {
                continue;
            }

            var info = DlnaDescriptionParser.Parse(xml, location);
            if (info is null)
            {
                continue; // Not a MediaRenderer / no AVTransport.
            }

            var key = info.Udn.Length > 0 ? info.Udn : location;
            if (!seen.Add(key))
            {
                continue;
            }

            devices.Add(new DlnaDevice(info));
        }

        var result = devices.Select(d => d.Info).ToList();
        DevicesFound?.Invoke(this, result);
        return result;
    }
}

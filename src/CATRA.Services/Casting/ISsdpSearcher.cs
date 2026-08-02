namespace CATRA.Services.Casting;

/// <summary>
/// SSDP transport abstraction (ST-08) so <see cref="DlnaDiscoveryService"/> is
/// testable without real UDP multicast: production uses
/// <see cref="SsdpUdpSearcher"/>; tests inject canned responses.
/// </summary>
public interface ISsdpSearcher
{
    /// <summary>
    /// Multicasts M-SEARCH for <paramref name="searchTarget"/> and collects
    /// replies until <paramref name="timeout"/> elapses.
    /// </summary>
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(
        string searchTarget,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

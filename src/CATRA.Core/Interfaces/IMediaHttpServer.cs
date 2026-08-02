namespace CATRA.Core.Interfaces;

/// <summary>
/// Embedded HTTP server (Kestrel) that serves media files to DLNA renderers
/// with byte-range support (ST-08, RF-06). Files are exposed under opaque GUID
/// tokens — the real path never leaves the process (anti path-traversal).
/// </summary>
public interface IMediaHttpServer
{
    /// <summary>Whether the server is currently listening.</summary>
    bool IsRunning { get; }

    /// <summary>The effective TCP port (random/ephemeral when started with 0).</summary>
    int Port { get; }

    /// <summary>Starts listening. Idempotent.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops listening and clears all registrations. Idempotent.</summary>
    Task StopAsync();

    /// <summary>
    /// Registers <paramref name="filePath"/> under a fresh GUID token and
    /// returns the token. The file is read (not moved); it must stay readable
    /// for the duration of the streaming session.
    /// </summary>
    string RegisterFile(string filePath, string contentType);

    /// <summary>Removes a registration. Returns <c>false</c> when the token is unknown.</summary>
    bool UnregisterFile(string token);

    /// <summary>
    /// The LAN-reachable URL for a registered token
    /// (<c>http://{localIp}:{port}/media/{token}</c>).
    /// </summary>
    string GetMediaUrl(string token);

    /// <summary>Best-effort LAN IPv4 address of this machine (never a loopback when avoidable).</summary>
    string GetLocalIp();
}

namespace CATRA.Core.Interfaces;

/// <summary>
/// Standalone Kestrel HTTP server that hosts the web control panel (ST-10).
/// Serves static files, a REST API for state snapshots and a WebSocket endpoint
/// for real-time bidirectional communication with browser clients.
/// </summary>
public interface IWebControlServer
{
    /// <summary>
    /// Whether the server is currently listening for connections.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// The TCP port the server is bound to.
    /// </summary>
    int Port { get; }

    /// <summary>
    /// Starts the Kestrel host. Idempotent — calling when already running is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the Kestrel host and releases all resources. Idempotent.
    /// </summary>
    Task StopAsync();
}

using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Service that bridges the player engine with the web control panel (ST-10).
/// Exposes the current state, processes incoming commands and raises events
/// that the hub implementation subscribes to for pushing updates.
/// </summary>
public interface IWebControlService
{
    /// <summary>
    /// Builds a snapshot of the current player state suitable for sending
    /// to web control clients.
    /// </summary>
    /// <returns>The current <see cref="WebControlState"/>.</returns>
    WebControlState GetCurrentState();

    /// <summary>
    /// Processes a command received from a web control client.
    /// Translates the <see cref="WebControlCommand"/> into the appropriate
    /// player-engine call (play, pause, seek, volume, etc.).
    /// </summary>
    /// <param name="command">The command to handle.</param>
    Task HandleCommandAsync(WebControlCommand command);

    /// <summary>
    /// Raised when the full player state changes (play/pause, track change, queue change, etc.).
    /// Subscribers should call <see cref="GetCurrentState"/> and push via <see cref="IWebControlHub"/>.
    /// </summary>
    event EventHandler<WebControlState>? StateChanged;

    /// <summary>
    /// Raised at ~1 s cadence with the current playback position and duration.
    /// Subscribers should forward via <see cref="IWebControlHub.BroadcastPositionAsync"/>.
    /// </summary>
    event EventHandler<(double Position, double Duration)>? PositionChanged;

    /// <summary>
    /// Informs the service which episode is currently loaded in the player.
    /// Called by the PlayerViewModel when casting starts or the episode changes.
    /// </summary>
    /// <param name="episodeId">Database id of the current episode.</param>
    void SetCurrentEpisode(int episodeId);

    // ── Player client management (browser mode) ──────────────

    /// <summary>
    /// Connection id of the designated player client, or <c>null</c> when none.
    /// </summary>
    string? PlayerClientId { get; }

    /// <summary>
    /// Sets or clears the player client connection id.
    /// Called by the WebSocket handler when a client identifies itself as the player.
    /// </summary>
    void SetPlayerClient(string? clientId);

    /// <summary>
    /// Raised when a command must be relayed to the player client over WebSocket
    /// (e.g. play, pause, seek instructions originating from a remote control client).
    /// </summary>
    event EventHandler<WebControlCommand>? CommandForPlayer;

    /// <summary>
    /// Raised when library browse data is ready to be sent back to the requesting
    /// client. The tuple carries the target kind (<c>categories</c>, <c>items</c>,
    /// <c>episodes</c>) and the resolved data object.
    /// </summary>
    event EventHandler<(string Target, object Data)>? LibraryDataReady;
}

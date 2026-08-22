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
}

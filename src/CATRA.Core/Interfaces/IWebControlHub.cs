using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Abstraction over the WebSocket transport that pushes state updates
/// to connected web control clients (ST-10).
/// Implementations handle serialisation and connection management.
/// </summary>
public interface IWebControlHub
{
    /// <summary>
    /// Sends the complete player state to all connected clients.
    /// </summary>
    /// <param name="state">Full state snapshot to broadcast.</param>
    Task SendFullStateAsync(WebControlState state);

    /// <summary>
    /// Broadcasts a lightweight position/duration update (high-frequency, ~1 s cadence).
    /// </summary>
    /// <param name="position">Current playback position in seconds.</param>
    /// <param name="duration">Total media duration in seconds.</param>
    Task BroadcastPositionAsync(double position, double duration);

    /// <summary>
    /// Notifies connected clients that the playback queue has changed.
    /// </summary>
    /// <param name="queue">The updated queue.</param>
    Task NotifyQueueChangedAsync(IReadOnlyList<WebControlQueueItem> queue);

    /// <summary>
    /// Sends a raw text message to a specific connection identified by <paramref name="connectionId"/>.
    /// No-op when the connection id is not found (client already disconnected).
    /// </summary>
    /// <param name="connectionId">Target connection id.</param>
    /// <param name="message">Pre-serialized JSON message to send.</param>
    Task SendToConnectionAsync(string connectionId, string message);

    /// <summary>
    /// Sends library browse data to a specific connection.
    /// The message is shaped as <c>{"type":"library","target":"…","items":…}</c>.
    /// </summary>
    /// <param name="connectionId">Target connection id.</param>
    /// <param name="target">Browse target kind (categories, items, episodes, etc.).</param>
    /// <param name="data">The resolved data payload.</param>
    Task NotifyLibraryDataAsync(string connectionId, string target, object data);
}

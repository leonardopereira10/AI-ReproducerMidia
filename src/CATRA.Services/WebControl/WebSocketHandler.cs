using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using Microsoft.AspNetCore.Http;

namespace CATRA.Services.WebControl;

/// <summary>
/// Manages WebSocket connections for the web control panel (ST-10).
/// Implements <see cref="IWebControlHub"/> to broadcast state updates
/// and processes incoming commands via <see cref="IWebControlService"/>.
/// <para>
/// Thread-safety: connections are stored in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Per-connection sends are serialized via <see cref="SemaphoreSlim"/> to prevent
/// interleaved writes from concurrent broadcast sources.
/// </para>
/// </summary>
public sealed class WebSocketHandler : IWebControlHub, IAsyncDisposable
{
    private readonly IWebControlService _service;
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly object _timerLock = new();
    private PeriodicTimer? _positionTimer;
    private CancellationTokenSource? _positionTimerCts;
    private bool _disposed;
    private int _connectionIdCounter;

    /// <summary>
    /// Connection id of the client that issued the most recent <c>browse</c> command.
    /// Used to route the asynchronous <see cref="IWebControlService.LibraryDataReady"/>
    /// response back to the correct connection. Cleared after the response is sent.
    /// </summary>
    private string? _pendingBrowseConnectionId;

    /// <summary>
    /// Creates a new handler wired to the given <see cref="IWebControlService"/>.
    /// Subscribes to <see cref="IWebControlService.StateChanged"/> and
    /// <see cref="IWebControlService.PositionChanged"/> for push updates.
    /// </summary>
    public WebSocketHandler(IWebControlService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StateChanged += OnStateChanged;
        _service.PositionChanged += OnPositionChanged;
        _service.CommandForPlayer += OnCommandForPlayer;
        _service.LibraryDataReady += OnLibraryDataReady;
    }

    /// <summary>
    /// Gets the number of currently active WebSocket connections.
    /// </summary>
    public int ActiveConnections => _connections.Count;

    // ════════════════════════════════════════════════════════════
    //  Connection handling (called from WebControlServer)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Accepts a WebSocket connection from the HTTP context, sends an initial
    /// state snapshot, and enters the receive loop until the client disconnects.
    /// </summary>
    /// <param name="context">The HTTP context containing the WebSocket upgrade request.</param>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connectionId = Interlocked.Increment(ref _connectionIdCounter).ToString();
        var connection = new ClientConnection(connectionId, ws);

        _connections.TryAdd(connectionId, connection);

        try
        {
            // Send initial state snapshot as first message
            var state = _service.GetCurrentState();
            await SendToConnectionAsync(connection, SerializeStateMessage(state), context.RequestAborted)
                .ConfigureAwait(false);

            // Start position timer if currently playing
            EnsurePositionTimer(state.IsPlaying);

            // Receive loop: process incoming commands
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleIncomingMessageAsync(message, connectionId).ConfigureAwait(false);
                }
            }
        }
        catch (WebSocketException)
        {
            // Connection closed unexpectedly — cleanup in finally
        }
        catch (OperationCanceledException)
        {
            // Request aborted (client disconnected or server shutting down)
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            connection.Dispose();

            // Clear player client registration when the player disconnects
            if (_service.PlayerClientId == connectionId)
            {
                _service.SetPlayerClient(null);
            }

            // Clear pending browse if this connection was waiting for a library response
            if (_pendingBrowseConnectionId == connectionId)
            {
                _pendingBrowseConnectionId = null;
            }

            // Stop position timer if no connections remain
            if (_connections.IsEmpty)
            {
                StopPositionTimer();
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  IWebControlHub — broadcast methods
    // ════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task SendFullStateAsync(WebControlState state)
    {
        var message = SerializeStateMessage(state);
        await BroadcastAsync(message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BroadcastPositionAsync(double position, double duration)
    {
        // Manual JSON for lightweight position message (avoid allocation of anonymous type)
        var message = /* lang=json */ $$"""{"type":"position","position":{{position.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"duration":{{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";
        await BroadcastAsync(message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task NotifyQueueChangedAsync(IReadOnlyList<WebControlQueueItem> queue)
    {
        var payload = new QueueMessage("queue", queue);
        var message = JsonSerializer.Serialize(payload, _jsonOptions);
        await BroadcastAsync(message).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendToConnectionAsync(string connectionId, string message)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            try
            {
                await SendToConnectionAsync(connection, message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Remove defective connection
                _connections.TryRemove(connectionId, out _);
                connection.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async Task NotifyLibraryDataAsync(string connectionId, string target, object data)
    {
        var message = SerializeLibraryMessage(target, data);
        await SendToConnectionAsync(connectionId, message).ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════
    //  Event subscriptions (IWebControlService)
    // ════════════════════════════════════════════════════════════

    private async void OnStateChanged(object? sender, WebControlState state)
    {
        try
        {
            await SendFullStateAsync(state).ConfigureAwait(false);
            EnsurePositionTimer(state.IsPlaying);
        }
        catch
        {
            // Broadcast failures are non-fatal — don't crash the event publisher
        }
    }

    private async void OnPositionChanged(object? sender, (double Position, double Duration) pos)
    {
        try
        {
            await BroadcastPositionAsync(pos.Position, pos.Duration).ConfigureAwait(false);
        }
        catch
        {
            // Broadcast failures are non-fatal
        }
    }

    /// <summary>
    /// Relays a command to the designated player client connection.
    /// The message is wrapped as <c>{"type":"command","command":{…}}</c>.
    /// </summary>
    private async void OnCommandForPlayer(object? sender, WebControlCommand command)
    {
        try
        {
            var playerConnId = _service.PlayerClientId;
            if (playerConnId is not null)
            {
                var message = SerializeCommandRelayMessage(command);
                await SendToConnectionAsync(playerConnId, message).ConfigureAwait(false);
            }
        }
        catch
        {
            // Relay failures are non-fatal
        }
    }

    /// <summary>
    /// Sends library browse data to the connection that issued the <c>browse</c> request.
    /// Falls back to broadcast when no pending connection is tracked.
    /// </summary>
    private async void OnLibraryDataReady(object? sender, (string Target, object Data) args)
    {
        try
        {
            var connId = Interlocked.Exchange(ref _pendingBrowseConnectionId, null);
            if (connId is not null)
            {
                await NotifyLibraryDataAsync(connId, args.Target, args.Data).ConfigureAwait(false);
            }
            else
            {
                // Fallback: broadcast to all connections
                var message = SerializeLibraryMessage(args.Target, args.Data);
                await BroadcastAsync(message).ConfigureAwait(false);
            }
        }
        catch
        {
            // Send failures are non-fatal
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Periodic position timer
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the position broadcast timer when <paramref name="isPlaying"/> is true
    /// and connections exist. Stops it when playback is not active.
    /// </summary>
    private void EnsurePositionTimer(bool isPlaying)
    {
        lock (_timerLock)
        {
            if (isPlaying && !_connections.IsEmpty && _positionTimer is null)
            {
                _positionTimerCts = new CancellationTokenSource();
                _positionTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                var ct = _positionTimerCts.Token;
                _ = Task.Run(async () => await RunPositionTimerLoopAsync(ct).ConfigureAwait(false), ct);
            }
            else if (!isPlaying)
            {
                StopPositionTimerUnsafe();
            }
        }
    }

    private async Task RunPositionTimerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _positionTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var state = _service.GetCurrentState();
                    if (state.IsPlaying)
                    {
                        await BroadcastPositionAsync(state.Position, state.Duration).ConfigureAwait(false);
                    }
                    else
                    {
                        // State changed to not-playing — stop the timer
                        lock (_timerLock)
                        {
                            StopPositionTimerUnsafe();
                        }
                        break;
                    }
                }
                catch
                {
                    // Non-fatal — continue ticking
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timer was disposed/cancelled
        }
    }

    /// <summary>
    /// Stops and disposes the position timer. Must be called under <see cref="_timerLock"/>.
    /// </summary>
    private void StopPositionTimerUnsafe()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
        _positionTimerCts?.Cancel();
        _positionTimerCts?.Dispose();
        _positionTimerCts = null;
    }

    private void StopPositionTimer()
    {
        lock (_timerLock)
        {
            StopPositionTimerUnsafe();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Broadcast & send helpers
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends <paramref name="message"/> to all active connections.
    /// Removes connections that fail to receive.
    /// </summary>
    private async Task BroadcastAsync(string message)
    {
        // Snapshot keys to avoid modifying collection during iteration
        foreach (var kvp in _connections)
        {
            var connection = kvp.Value;
            try
            {
                await SendToConnectionAsync(connection, message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Remove defective connection
                _connections.TryRemove(kvp.Key, out _);
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Sends a text message to a single connection, serialized via the connection's semaphore.
    /// </summary>
    private static async Task SendToConnectionAsync(ClientConnection connection, string message, CancellationToken ct)
    {
        await connection.SendSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (connection.WebSocket.State == WebSocketState.Open)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                await connection.WebSocket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.SendSemaphore.Release();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Incoming message processing
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a JSON message into a <see cref="WebControlCommand"/> and dispatches
    /// it to <see cref="IWebControlService.HandleCommandAsync"/>. Routes player-client
    /// registration (<c>playEpisode</c>) and targeted browse responses based on
    /// <paramref name="connectionId"/>.
    /// Silently ignores malformed JSON.
    /// </summary>
    private async Task HandleIncomingMessageAsync(string json, string connectionId)
    {
        try
        {
            var command = WebControlCommand.FromJson(json);

            // Commands that mark this client as the designated player
            if (command.Type == "playEpisode")
            {
                _service.SetPlayerClient(connectionId);
                if (_connections.TryGetValue(connectionId, out var conn))
                {
                    conn.PlayerClientId = connectionId;
                }
            }

            // Browse: track which connection requested data so the async
            // LibraryDataReady response is routed to the correct client.
            if (command.Type == "browse")
            {
                _pendingBrowseConnectionId = connectionId;
            }

            await _service.HandleCommandAsync(command).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Ignore malformed messages — don't crash the receive loop
        }
        catch (Exception)
        {
            // Other deserialization errors — also ignore
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Serialization helpers
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Serializes a full state message: <c>{"type":"state","data":{...}}</c>.
    /// </summary>
    private string SerializeStateMessage(WebControlState state)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", "state");
        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, state, _jsonOptions);
        writer.WriteEndObject();

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serializes a library data message: <c>{"type":"library","target":"…","items":…}</c>.
    /// </summary>
    private string SerializeLibraryMessage(string target, object data)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", "library");
        writer.WriteString("target", target);
        writer.WritePropertyName("items");
        JsonSerializer.Serialize(writer, data, _jsonOptions);
        writer.WriteEndObject();

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serializes a command relay message: <c>{"type":"command","command":{…}}</c>.
    /// </summary>
    private string SerializeCommandRelayMessage(WebControlCommand command)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", "command");
        writer.WritePropertyName("command");
        JsonSerializer.Serialize(writer, command, _jsonOptions);
        writer.WriteEndObject();

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // ════════════════════════════════════════════════════════════
    //  IDisposable
    // ════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _service.StateChanged -= OnStateChanged;
        _service.PositionChanged -= OnPositionChanged;
        _service.CommandForPlayer -= OnCommandForPlayer;
        _service.LibraryDataReady -= OnLibraryDataReady;

        StopPositionTimer();

        // Close and dispose all active connections
        foreach (var kvp in _connections)
        {
            _connections.TryRemove(kvp.Key, out _);
            kvp.Value.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════
    //  Inner types
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Wraps a single WebSocket connection with a per-connection send semaphore
    /// to prevent interleaved writes from concurrent broadcast sources.
    /// </summary>
    private sealed class ClientConnection : IDisposable
    {
        public string Id { get; }
        public WebSocket WebSocket { get; }
        public SemaphoreSlim SendSemaphore { get; } = new(1, 1);

        /// <summary>
        /// Set when this connection is the designated player client.
        /// Mirrors <see cref="IWebControlService.PlayerClientId"/> for quick lookup.
        /// </summary>
        public string? PlayerClientId { get; set; }

        public ClientConnection(string id, WebSocket webSocket)
        {
            Id = id;
            WebSocket = webSocket;
        }

        public void Dispose()
        {
            SendSemaphore.Dispose();

            if (WebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    WebSocket.Abort();
                }
                catch
                {
                    // Already closed — ignore
                }
            }

            WebSocket.Dispose();
        }
    }

    /// <summary>
    /// DTO for queue change notification: <c>{"type":"queue","items":[...]}</c>.
    /// </summary>
    private sealed record QueueMessage(string Type, IReadOnlyList<WebControlQueueItem> Items);
}

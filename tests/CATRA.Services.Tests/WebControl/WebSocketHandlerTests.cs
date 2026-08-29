using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Services.WebControl;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CATRA.Services.Tests.WebControl;

/// <summary>
/// Integration-style unit tests for <see cref="WebSocketHandler"/> (ST-10).
/// Spins up a real Kestrel endpoint on a loopback port and drives it with
/// <see cref="ClientWebSocket"/> instances; the web-control service itself is
/// a fake, so no casting/domain logic runs. Covers initial-state push,
/// broadcast to multiple clients, command parsing (valid + malformed) and
/// connection cleanup.
/// </summary>
public sealed class WebSocketHandlerTests : IAsyncLifetime
{
    private FakeWebControlService _service = null!;
    private WebSocketHandler _handler = null!;
    private WebApplication _app = null!;
    private Uri _wsUri = null!;
    private Uri _httpUri = null!;

    public async Task InitializeAsync()
    {
        _service = new FakeWebControlService();
        _handler = new WebSocketHandler(_service);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws", (HttpContext context) => _handler.HandleAsync(context));

        await _app.StartAsync();

        var bound = new Uri(_app.Urls.First());
        _httpUri = bound;
        _wsUri = new UriBuilder("ws", bound.Host, bound.Port, "/ws").Uri;
    }

    public async Task DisposeAsync()
    {
        await _handler.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<ClientWebSocket> ConnectClientAsync()
    {
        var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(_wsUri, cts.Token);
        return client;
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task SendTextAsync(WebSocket socket, string message)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException($"Condition not met within 5s: {description}");
            }

            await Task.Delay(25);
        }
    }

    private static WebControlState StateWithTitle(string title) => new(
        IsPlaying: false,
        IsPaused: false,
        Position: 0,
        Duration: 100,
        Volume: 100,
        Title: title,
        ThumbnailUrl: null,
        SkipIntroSec: 0,
        CanSkipIntro: false,
        HasNextEpisode: false,
        HasPreviousEpisode: false,
        CastDeviceName: null,
        ProfileLabel: null,
        Queue: []);

    // ── Initial state ───────────────────────────────────────────

    [Fact]
    public async Task Connect_ReceivesInitialStateSnapshot()
    {
        // Arrange
        _service.CurrentState = StateWithTitle("Piloto");

        // Act
        using var client = await ConnectClientAsync();
        var firstMessage = await ReceiveTextAsync(client);

        // Assert
        firstMessage.Should().Contain("\"type\":\"state\"");
        firstMessage.Should().Contain("Piloto");
    }

    [Fact]
    public async Task NonWebSocketRequest_Returns400()
    {
        // Arrange
        using var http = new HttpClient { BaseAddress = _httpUri };

        // Act
        var response = await http.GetAsync("/ws");

        // Assert
        ((int)response.StatusCode).Should().Be(400);
    }

    // ── Broadcast ───────────────────────────────────────────────

    [Fact]
    public async Task StateChanged_BroadcastsToAllConnectedClients()
    {
        // Arrange
        using var clientA = await ConnectClientAsync();
        using var clientB = await ConnectClientAsync();
        await ReceiveTextAsync(clientA); // drain initial snapshot
        await ReceiveTextAsync(clientB);
        _handler.ActiveConnections.Should().Be(2);

        // Act
        _service.RaiseStateChanged(StateWithTitle("Episodio 2"));

        // Assert
        var messageA = await ReceiveTextAsync(clientA);
        var messageB = await ReceiveTextAsync(clientB);
        messageA.Should().Contain("Episodio 2");
        messageB.Should().Contain("Episodio 2");
    }

    [Fact]
    public async Task BroadcastPosition_ReachesAllClients()
    {
        // Arrange
        using var clientA = await ConnectClientAsync();
        using var clientB = await ConnectClientAsync();
        await ReceiveTextAsync(clientA); // drain initial snapshot
        await ReceiveTextAsync(clientB);

        // Act
        await _handler.BroadcastPositionAsync(12.5, 100);

        // Assert
        var messageA = await ReceiveTextAsync(clientA);
        var messageB = await ReceiveTextAsync(clientB);
        messageA.Should().Contain("\"type\":\"position\"").And.Contain("12.5");
        messageB.Should().Contain("\"type\":\"position\"");
    }

    [Fact]
    public async Task NotifyQueueChanged_BroadcastsQueuePayload()
    {
        // Arrange
        using var client = await ConnectClientAsync();
        await ReceiveTextAsync(client); // drain initial snapshot

        // Act
        await _handler.NotifyQueueChangedAsync([new WebControlQueueItem(3, "Ep 3", 200, true)]);

        // Assert
        var message = await ReceiveTextAsync(client);
        message.Should().Contain("\"type\":\"queue\"").And.Contain("Ep 3");
    }

    // ── Incoming commands ───────────────────────────────────────

    [Fact]
    public async Task ValidCommand_IsDispatchedToService()
    {
        // Arrange
        using var client = await ConnectClientAsync();
        await ReceiveTextAsync(client); // drain initial snapshot

        // Act
        await SendTextAsync(client, """{"type":"seek","position":45.5}""");

        // Assert
        await WaitUntilAsync(() => _service.Commands.Count == 1, "command dispatched");
        _service.Commands[0].Type.Should().Be("seek");
        _service.Commands[0].Position.Should().Be(45.5);
    }

    [Fact]
    public async Task MalformedJson_DoesNotCrash_AndConnectionStaysUsable()
    {
        // Arrange
        using var client = await ConnectClientAsync();
        await ReceiveTextAsync(client); // drain initial snapshot

        // Act — garbage first, then a valid command.
        await SendTextAsync(client, "this is not json {{{");
        await SendTextAsync(client, """{"type":"pause"}""");

        // Assert
        await WaitUntilAsync(() => _service.Commands.Count == 1, "valid command after garbage");
        _service.Commands.Should().ContainSingle().Which.Type.Should().Be("pause");
        client.State.Should().Be(WebSocketState.Open);
    }

    // ── Cleanup ─────────────────────────────────────────────────

    [Fact]
    public async Task ClosedConnection_IsRemovedFromActiveConnections()
    {
        // Arrange
        var client = await ConnectClientAsync();
        await ReceiveTextAsync(client); // drain initial snapshot
        _handler.ActiveConnections.Should().Be(1);

        // Act — graceful close from the client side.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        client.Dispose();

        // Assert — server receive loop exits and removes the connection.
        await WaitUntilAsync(() => _handler.ActiveConnections == 0, "connection removed");
    }

    [Fact]
    public async Task FailedConnection_IsRemovedOnBroadcast()
    {
        // Arrange — connect then abort the socket without a close handshake.
        var client = await ConnectClientAsync();
        await ReceiveTextAsync(client); // drain initial snapshot
        _handler.ActiveConnections.Should().Be(1);
        client.Abort();
        client.Dispose();

        // Act — broadcast; the failing send triggers removal.
        await WaitUntilAsync(() =>
        {
            _handler.BroadcastPositionAsync(1, 10).GetAwaiter().GetResult();
            return _handler.ActiveConnections == 0;
        }, "defective connection removed on broadcast");

        // Assert
        _handler.ActiveConnections.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════
    //  Fake service
    // ════════════════════════════════════════════════════════════

    private sealed class FakeWebControlService : IWebControlService
    {
        public WebControlState CurrentState { get; set; } = new(
            IsPlaying: false,
            IsPaused: false,
            Position: 0,
            Duration: 0,
            Volume: 100,
            Title: "Idle",
            ThumbnailUrl: null,
            SkipIntroSec: 0,
            CanSkipIntro: false,
            HasNextEpisode: false,
            HasPreviousEpisode: false,
            CastDeviceName: null,
            ProfileLabel: null,
            Queue: []);

        public List<WebControlCommand> Commands { get; } = [];

        public string? PlayerClientId { get; set; }

        public event EventHandler<WebControlState>? StateChanged;
        public event EventHandler<(double Position, double Duration)>? PositionChanged;
        public event EventHandler<WebControlCommand>? CommandForPlayer;
        public event EventHandler<(string Target, object Data)>? LibraryDataReady;

        public WebControlState GetCurrentState() => CurrentState;

        public Task HandleCommandAsync(WebControlCommand command)
        {
            lock (Commands)
            {
                Commands.Add(command);
            }

            return Task.CompletedTask;
        }

        public void SetCurrentEpisode(int episodeId)
        {
        }

        public void SetPlayerClient(string? clientId)
        {
            PlayerClientId = clientId;
        }

        public void RaiseCommandForPlayer(WebControlCommand command)
            => CommandForPlayer?.Invoke(this, command);

        public void RaiseLibraryDataReady(string target, object data)
            => LibraryDataReady?.Invoke(this, (target, data));

        public void RaiseStateChanged(WebControlState state)
        {
            CurrentState = state;
            StateChanged?.Invoke(this, state);
        }

        public void RaisePositionChanged(double position, double duration)
            => PositionChanged?.Invoke(this, (position, duration));
    }
}

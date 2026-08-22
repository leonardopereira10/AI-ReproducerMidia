using System.Net;
using System.Reflection;
using System.Text.Json;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;

namespace CATRA.Services.WebControl;

/// <summary>
/// Embedded Kestrel HTTP server that hosts the web control panel (ST-10).
/// Serves static files from embedded resources, exposes a REST API for state
/// snapshots and accepts WebSocket connections for real-time communication.
/// Follows the same lifecycle pattern as <c>MediaHttpServer</c>.
/// </summary>
public sealed class WebControlServer : IWebControlServer, IAsyncDisposable
{
    /// <summary>Default TCP port for the web control panel.</summary>
    public const int DefaultPort = 5050;

    /// <summary>Embedded resource root prefix.</summary>
    private const string ResourcePrefix = "CATRA.Services.WebControl.wwwroot.";

    private readonly int _requestedPort;
    private readonly IWebControlService? _webControlService;
    private readonly WebSocketHandler? _webSocketHandler;

    private IWebHost? _host;
    private int _port;

    /// <summary>
    /// Creates the server bound to <c>0.0.0.0</c> on the specified port.
    /// </summary>
    /// <param name="port">TCP port (default 5050).</param>
    /// <param name="webControlService">
    /// Optional service for state snapshots. When <c>null</c>, <c>/api/state</c>
    /// returns an empty/default state.
    /// </param>
    /// <param name="webSocketHandler">
    /// Optional WebSocket handler for real-time communication (subtask_04).
    /// When <c>null</c>, <c>/ws</c> falls back to the placeholder echo behaviour.
    /// </param>
    public WebControlServer(
        int port = DefaultPort,
        IWebControlService? webControlService = null,
        WebSocketHandler? webSocketHandler = null)
    {
        _requestedPort = port;
        _webControlService = webControlService;
        _webSocketHandler = webSocketHandler;
    }

    /// <inheritdoc />
    public bool IsRunning => _host is not null;

    /// <inheritdoc />
    public int Port => _port;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            return;
        }

        var host = new WebHostBuilder()
            .UseKestrel(options => options.Listen(IPAddress.Any, _requestedPort))
            .Configure(app =>
            {
                app.UseWebSockets();
                app.Run(HandleRequestAsync);
            })
            .Build();

        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = host.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0 ||
            !Uri.TryCreate(addresses.First(), UriKind.Absolute, out var uri))
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Kestrel não reportou o endereço de escuta.");
        }

        _port = uri.Port;
        _host = host;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        var host = _host;
        _host = null;
        _port = 0;

        if (host is not null)
        {
            await host.StopAsync().ConfigureAwait(false);
            host.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ──────────────────────────────────────────────────────────
    //  Request dispatcher
    // ──────────────────────────────────────────────────────────

    private Task HandleRequestAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;

        return path switch
        {
            "/" => HandleIndexAsync(ctx),
            "/css/style.css" => HandleCssAsync(ctx),
            "/js/app.js" => HandleJsAsync(ctx),
            "/api/state" => HandleApiStateAsync(ctx),
            "/ws" => HandleWebSocketAsync(ctx),
            _ => HandleNotFoundAsync(ctx),
        };
    }

    private static Task HandleNotFoundAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────
    //  Static file handlers (embedded resources)
    // ──────────────────────────────────────────────────────────

    private static async Task HandleIndexAsync(HttpContext ctx)
        => await ServeEmbeddedResourceAsync(ctx, ResourcePrefix + "index.html", "text/html")
            .ConfigureAwait(false);

    private static async Task HandleCssAsync(HttpContext ctx)
        => await ServeEmbeddedResourceAsync(ctx, ResourcePrefix + "css.style.css", "text/css")
            .ConfigureAwait(false);

    private static async Task HandleJsAsync(HttpContext ctx)
        => await ServeEmbeddedResourceAsync(ctx, ResourcePrefix + "js.app.js", "application/javascript")
            .ConfigureAwait(false);

    // ──────────────────────────────────────────────────────────
    //  REST API
    // ──────────────────────────────────────────────────────────

    private async Task HandleApiStateAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json";

        WebControlState state;
        if (_webControlService is not null)
        {
            state = _webControlService.GetCurrentState();
        }
        else
        {
            // Return an empty/default state when no service is wired up.
            state = new WebControlState(
                IsPlaying: false,
                IsPaused: false,
                Position: 0,
                Duration: 0,
                Volume: 0,
                Title: string.Empty,
                ThumbnailUrl: null,
                SkipIntroSec: 0,
                CanSkipIntro: false,
                HasNextEpisode: false,
                HasPreviousEpisode: false,
                CastDeviceName: null,
                ProfileLabel: null,
                Queue: []);
        }

        await JsonSerializer.SerializeAsync(ctx.Response.Body, state, JsonOptions).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────
    //  WebSocket (delegated to WebSocketHandler)
    // ──────────────────────────────────────────────────────────

    private Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (_webSocketHandler is not null)
        {
            return _webSocketHandler.HandleAsync(ctx);
        }

        // Fallback: reject when no handler is wired up.
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    private static async Task ServeEmbeddedResourceAsync(
        HttpContext ctx,
        string resourceName,
        string contentType)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = stream.Length;
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

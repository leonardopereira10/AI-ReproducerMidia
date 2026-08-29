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
    private readonly ILibraryApiService? _libraryApiService;
    private readonly IStreamService? _streamService;
    private readonly IEpisodeRepository? _episodeRepository;
    private readonly IMediaItemRepository? _mediaItemRepository;

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
    /// <param name="libraryApiService">
    /// Optional library API service for media library queries.
    /// When <c>null</c>, <c>/api/library/*</c> routes return 503.
    /// </param>
    /// <param name="streamService">
    /// Optional stream service for profile queries.
    /// When <c>null</c>, <c>/api/library/episodes/{id}/profiles</c> returns 503.
    /// </param>
    /// <param name="episodeRepository">
    /// Optional episode repository for thumbnail serving.
    /// When <c>null</c>, <c>/api/thumbnail/{id}</c> returns 503.
    /// </param>
    /// <param name="mediaItemRepository">
    /// Optional media item repository for poster serving.
    /// When <c>null</c>, <c>/api/poster/{id}</c> returns 503.
    /// </param>
    public WebControlServer(
        int port = DefaultPort,
        IWebControlService? webControlService = null,
        WebSocketHandler? webSocketHandler = null,
        ILibraryApiService? libraryApiService = null,
        IStreamService? streamService = null,
        IEpisodeRepository? episodeRepository = null,
        IMediaItemRepository? mediaItemRepository = null)
    {
        _requestedPort = port;
        _webControlService = webControlService;
        _webSocketHandler = webSocketHandler;
        _libraryApiService = libraryApiService;
        _streamService = streamService;
        _episodeRepository = episodeRepository;
        _mediaItemRepository = mediaItemRepository;
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

        // ── Thumbnail / Poster image routes ──
        if (TryExtractSimpleId(path, "/api/thumbnail/", out var thumbEpisodeId))
            return HandleApiThumbnailAsync(ctx, thumbEpisodeId);
        if (TryExtractSimpleId(path, "/api/poster/", out var posterItemId))
            return HandleApiPosterAsync(ctx, posterItemId);

        // ── Library API routes with path params (prefix matching) ──
        if (TryExtractId(path, "/api/library/categories/", "/items", out var catId))
            return HandleApiItemsAsync(ctx, catId);
        if (TryExtractId(path, "/api/library/items/", "/episodes", out var itemId))
            return HandleApiEpisodesAsync(ctx, itemId);
        if (TryExtractId(path, "/api/library/episodes/", "/profiles", out var epId))
            return HandleApiProfilesAsync(ctx, epId);

        // ── Library API routes (exact match) ──
        if (path == "/api/library/categories")
            return HandleApiCategoriesAsync(ctx);
        if (path == "/api/library/continue-watching")
            return HandleApiContinueWatchingAsync(ctx);
        if (path == "/api/library/search")
            return HandleApiSearchAsync(ctx);

        // ── Existing routes ──
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
                Queue: [],
                Mode: "idle",
                StreamUrl: null,
                SeriesTitle: null,
                AvailableProfiles: null,
                AvailableDevices: null,
                IsPlayerClient: false);
        }

        await JsonSerializer.SerializeAsync(ctx.Response.Body, state, JsonOptions).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────
    //  Library API handlers
    // ──────────────────────────────────────────────────────────

    private async Task HandleApiCategoriesAsync(HttpContext ctx)
    {
        if (_libraryApiService is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var categories = await _libraryApiService.GetCategoriesAsync().ConfigureAwait(false);
        await WriteJsonAsync(ctx, categories).ConfigureAwait(false);
    }

    private async Task HandleApiItemsAsync(HttpContext ctx, int categoryId)
    {
        if (_libraryApiService is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var items = await _libraryApiService.GetItemsByCategoryAsync(categoryId).ConfigureAwait(false);
        await WriteJsonAsync(ctx, items).ConfigureAwait(false);
    }

    private async Task HandleApiEpisodesAsync(HttpContext ctx, int mediaItemId)
    {
        if (_libraryApiService is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var episodes = await _libraryApiService.GetEpisodesByItemAsync(mediaItemId).ConfigureAwait(false);
        await WriteJsonAsync(ctx, episodes).ConfigureAwait(false);
    }

    private async Task HandleApiSearchAsync(HttpContext ctx)
    {
        if (_libraryApiService is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var query = ctx.Request.Query["q"].ToString();
        var results = await _libraryApiService.SearchAsync(query).ConfigureAwait(false);
        await WriteJsonAsync(ctx, results).ConfigureAwait(false);
    }

    private async Task HandleApiContinueWatchingAsync(HttpContext ctx)
    {
        if (_libraryApiService is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var items = await _libraryApiService.GetContinueWatchingAsync().ConfigureAwait(false);
        await WriteJsonAsync(ctx, items).ConfigureAwait(false);
    }

    private Task HandleApiProfilesAsync(HttpContext ctx, int episodeId)
    {
        if (_streamService is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        var profiles = _streamService.GetAvailableProfiles(episodeId);
        return WriteJsonAsync(ctx, profiles);
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
    //  Thumbnail / Poster image handlers
    // ──────────────────────────────────────────────────────────

    private async Task HandleApiThumbnailAsync(HttpContext ctx, int episodeId)
    {
        if (_episodeRepository is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var episode = _episodeRepository.GetById(episodeId);
        if (episode?.ThumbnailPath is null || !File.Exists(episode.ThumbnailPath))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await ServeImageFileAsync(ctx, episode.ThumbnailPath).ConfigureAwait(false);
    }

    private async Task HandleApiPosterAsync(HttpContext ctx, int itemId)
    {
        if (_mediaItemRepository is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var item = _mediaItemRepository.GetById(itemId);
        if (item?.CoverPath is null || !File.Exists(item.CoverPath))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await ServeImageFileAsync(ctx, item.CoverPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves an image file with proper Content-Type and byte-range support.
    /// </summary>
    private static async Task ServeImageFileAsync(HttpContext ctx, string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        ctx.Response.ContentType = extension switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

        var fileInfo = new FileInfo(filePath);
        var fileLength = fileInfo.Length;

        // Check for byte-range request
        var rangeHeader = ctx.Request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var rangeSpec = rangeHeader.Substring("bytes=".Length);
            var dashIdx = rangeSpec.IndexOf('-');
            if (dashIdx > 0 && long.TryParse(rangeSpec.Substring(0, dashIdx), out var rangeStart))
            {
                var rangeEnd = dashIdx < rangeSpec.Length - 1
                    && long.TryParse(rangeSpec.Substring(dashIdx + 1), out var parsedEnd)
                    ? parsedEnd
                    : fileLength - 1;

                rangeStart = Math.Clamp(rangeStart, 0, fileLength - 1);
                rangeEnd = Math.Clamp(rangeEnd, rangeStart, fileLength - 1);
                var contentLength = rangeEnd - rangeStart + 1;

                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.Headers["Content-Range"] = $"bytes {rangeStart}-{rangeEnd}/{fileLength}";
                ctx.Response.ContentLength = contentLength;
                ctx.Response.Headers["Accept-Ranges"] = "bytes";

                await using var fs = File.OpenRead(filePath);
                fs.Seek(rangeStart, SeekOrigin.Begin);
                var buffer = new byte[Math.Min(64 * 1024, contentLength)];
                var remaining = contentLength;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ctx.RequestAborted).ConfigureAwait(false);
                    if (read == 0) break;
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted).ConfigureAwait(false);
                    remaining -= read;
                }
                return;
            }
        }

        // Full file response
        ctx.Response.ContentLength = fileLength;
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        await using var fullStream = File.OpenRead(filePath);
        await fullStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts an integer ID from a simple path like <c>/prefix/{id}</c> (no suffix required).
    /// Returns <c>true</c> when the path matches and the ID parses.
    /// </summary>
    private static bool TryExtractSimpleId(string path, string prefix, out int id)
    {
        id = 0;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = path.Substring(prefix.Length);
        // Allow optional trailing slash
        if (remainder.EndsWith('/'))
            remainder = remainder.Substring(0, remainder.Length - 1);

        return int.TryParse(remainder, out id);
    }

    /// <summary>
    /// Extracts an integer ID from a path like <c>/prefix/{id}/suffix</c>.
    /// Returns <c>true</c> when the path matches the pattern and the ID parses.
    /// </summary>
    private static bool TryExtractId(string path, string prefix, string suffix, out int id)
    {
        id = 0;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = path.Substring(prefix.Length);
        var slashIdx = remainder.IndexOf('/');
        if (slashIdx < 0)
            return false;

        var idSegment = remainder.Substring(0, slashIdx);
        var afterId = remainder.Substring(slashIdx);

        if (!string.Equals(afterId, suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(idSegment, out id);
    }

    private static async Task WriteJsonAsync<T>(HttpContext ctx, T value)
    {
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, JsonOptions).ConfigureAwait(false);
    }

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

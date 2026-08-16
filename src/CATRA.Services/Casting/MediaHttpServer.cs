using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CATRA.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace CATRA.Services.Casting;

/// <summary>
/// Embedded Kestrel HTTP server that streams registered media files to DLNA
/// renderers with full byte-range support (ST-08, RF-06). Files are addressed
/// by opaque GUID tokens (<c>GET /media/{token}</c>) — the filesystem path is
/// never exposed, which also neutralizes path-traversal attempts.
/// </summary>
/// <remarks>
/// Responses: <c>200</c> (whole file), <c>206 Partial Content</c>
/// (<c>Content-Range: bytes s-e/total</c>) for <c>Range: bytes=s-e</c> /
/// <c>s-</c> / <c>-suffix</c>, <c>416</c> for unsatisfiable ranges,
/// <c>404</c> for unknown tokens. Always advertises <c>Accept-Ranges: bytes</c>.
/// </remarks>
public sealed class MediaHttpServer : IMediaHttpServer, IAsyncDisposable
{
    /// <summary>Route prefix for media tokens.</summary>
    public const string MediaPathPrefix = "/media/";

    private readonly string _bindAddress;
    private readonly int _requestedPort;
    private readonly ConcurrentDictionary<string, MediaRegistration> _registrations = new();

    private IWebHost? _host;
    private int _port;

    /// <summary>
    /// Creates the server. Defaults bind <c>0.0.0.0</c> on an ephemeral port
    /// (LAN-reachable); tests bind <c>127.0.0.1</c>.
    /// </summary>
    public MediaHttpServer(string bindAddress = "0.0.0.0", int port = 0)
    {
        _bindAddress = bindAddress;
        _requestedPort = port;
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
            .UseKestrel(options => options.Listen(IPAddress.Parse(_bindAddress), _requestedPort))
            .Configure(app => app.Run(HandleRequestAsync))
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
        _registrations.Clear();

        if (host is not null)
        {
            await host.StopAsync().ConfigureAwait(false);
            host.Dispose();
        }
    }

    /// <inheritdoc />
    public string RegisterFile(string filePath, string contentType)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var token = Guid.NewGuid().ToString("N");
        _registrations[token] = new MediaRegistration(filePath, contentType);
        return token;
    }

    /// <inheritdoc />
    public bool UnregisterFile(string token) => _registrations.TryRemove(token, out _);

    /// <inheritdoc />
    public string GetMediaUrl(string token)
        => $"http://{GetLocalIp()}:{Port}{MediaPathPrefix}{token}";

    /// <inheritdoc />
    public string GetLocalIp()
    {
        // Classic connectionless trick: the OS picks the egress interface for
        // this "destination" without sending any packet.
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            if (probe.LocalEndPoint is IPEndPoint local && !IPAddress.IsLoopback(local.Address))
            {
                return local.Address.ToString();
            }
        }
        catch (SocketException)
        {
            // Fall through to the interface scan.
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                    {
                        return address.Address.ToString();
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No usable adapter: fall back to loopback below.
        }

        return "127.0.0.1";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task HandleRequestAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith(MediaPathPrefix, StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var token = path[MediaPathPrefix.Length..];

        // Tokens are GUIDs: anything with extra segments is a traversal attempt.
        if (token.Length == 0 || token.Contains('/') || token.Contains('\\') ||
            !_registrations.TryGetValue(token, out var registration))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        long fileLength;
        try
        {
            fileLength = new FileInfo(registration.FilePath).Length;
        }
        catch (IOException)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (fileLength < 0 || !File.Exists(registration.FilePath))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        response.Headers.AcceptRanges = "bytes";
        response.ContentType = registration.ContentType;

        // 🌟 INJEÇÃO CRUCIAL PARA SMART TVs 4K:
        // O servidor HTTP precisa carimbar o mesmo perfil DLNA enviado no DIDL-Lite
        string dlnaFeatures = ResolveDlnaFeatures(registration.FilePath);
        if (!string.IsNullOrEmpty(dlnaFeatures))
        {
            response.Headers.Append("contentFeatures.dlna.org", dlnaFeatures);
        }

        var cancellationToken = context.RequestAborted;
        var rangeHeader = request.Headers.Range.ToString();

        if (string.IsNullOrWhiteSpace(rangeHeader) ||
            !TryParseRange(rangeHeader, fileLength, out var start, out var end))
        {
            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                // A Range header was present but unsatisfiable (start >= length).
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = new ContentRangeHeaderValue(fileLength).ToString();
                return;
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentLength = fileLength;
            if (HttpMethods.IsHead(request.Method) || fileLength == 0)
            {
                return;
            }

            await SendBytesAsync(response, registration.FilePath, 0, fileLength, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // --- COMPLEMENTO DO CÓDIGO TRUNCADO ---
        long count = end - start + 1;
        response.StatusCode = StatusCodes.Status206PartialContent;
        response.Headers.ContentRange = new ContentRangeHeaderValue(start, end, fileLength).ToString();
        response.ContentLength = count; // Definição correta do tamanho do chunk solicitado

        if (HttpMethods.IsHead(request.Method) || count == 0)
        {
            return;
        }

        // Entrega o pedaço exato (chunk) de bytes que a TV solicitou para processar o vídeo 4K
        await SendBytesAsync(response, registration.FilePath, start, count, cancellationToken)
            .ConfigureAwait(false);
    }

    private string ResolveDlnaFeatures(string filePath)
    {
        // Como seu app processa múltiplos vídeos, o ideal é mapear o perfil.
        // Para fins do seu teste atual com o vídeo 4K 60fps em HEVC:
        string ext = Path.GetExtension(filePath).ToLower();
        if (ext == ".mp4" || ext == ".mkv")
        {
            // Retorna a flag completa de tráfego de mídia UHD 60p para a TV aceitar o stream
            return "DLNA.ORG_PN=HEVC_MAIN_MP4_UHD_60p;DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        }

        // Fallback para AVC/H264 comum se não for um arquivo UHD conhecido
        return "DLNA.ORG_PN=AVC_MP4_EU_HD;DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
    }

    /// <summary>
    /// Parses a single-range <c>bytes=</c> specifier against
    /// <paramref name="fileLength"/>. Returns <c>false</c> when the range is
    /// unsatisfiable (start beyond EOF); multi-range is treated as no range by
    /// callers (malformed input also yields <c>false</c> with empty header check).
    /// </summary>
    internal static bool TryParseRange(string rangeHeader, long fileLength, out long start, out long end)
    {
        start = 0;
        end = fileLength - 1;

        if (fileLength == 0)
        {
            return false;
        }

        const string prefix = "bytes=";
        var span = rangeHeader.AsSpan().Trim();
        if (!span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = span[prefix.Length..].Trim();
        int comma = spec.IndexOf(',');
        if (comma >= 0)
        {
            spec = spec[..comma].Trim(); // multi-range: honor the first range only
        }

        int dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        try
        {
            if (startText.IsEmpty)
            {
                // Suffix range: "-N" = last N bytes.
                if (!long.TryParse(endText, out var suffix) || suffix <= 0)
                {
                    return false;
                }

                start = Math.Max(0, fileLength - suffix);
                end = fileLength - 1;
                return true;
            }

            if (!long.TryParse(startText, out start) || start < 0)
            {
                return false;
            }

            if (endText.IsEmpty)
            {
                end = fileLength - 1;
            }
            else if (!long.TryParse(endText, out end))
            {
                return false;
            }
            else if (end >= fileLength)
            {
                end = fileLength - 1; // RFC 7233: clamp overshooting end.
            }

            return start <= end && start < fileLength;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static async Task SendBytesAsync(
        HttpResponse response,
        string filePath,
        long start,
        long count,
        CancellationToken cancellationToken)
    {
        // ReadWrite share: the file may be the processed cache being written by
        // another job, or served to several renderers at once.
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break; // File shrank underneath: stop rather than over-read.
            }

            await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private sealed record MediaRegistration(string FilePath, string ContentType);
}

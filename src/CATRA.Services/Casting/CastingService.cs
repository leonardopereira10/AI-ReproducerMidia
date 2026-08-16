using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Casting;

/// <summary>
/// DLNA casting orchestrator (ST-08, RF-06): discovery → register file on the
/// embedded HTTP server → SetAVTransportURI (DIDL-Lite) + Play → 1s position
/// polling. Remote Play/Pause/Stop/Seek/Volume forward to the SOAP clients.
/// All state transitions are serialized through a gate; failures land in
/// <see cref="CastingState.Error"/> with <see cref="ErrorMessage"/>.
/// </summary>
public sealed class CastingService : ICastingService, IDisposable
{
    /// <summary>Default renderer position polling cadence.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly IDlnaDiscoveryService _discovery;
    private readonly IMediaHttpServer _httpServer;
    private readonly IAvTransportClient _avTransport;
    private readonly IRenderingControlClient _renderingControl;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DlnaDeviceInfo? _currentDevice;
    private string? _currentToken;
    private TimeSpan _currentDuration;
    private bool _mediaEndedFired;
    private CancellationTokenSource? _pollCts;
    private CastingState _state = CastingState.Idle;
    private bool _disposed;

    /// <summary>
    /// Creates the orchestrator. <paramref name="positionPollInterval"/>
    /// defaults to 1s; pass <see cref="TimeSpan.Zero"/> to disable polling
    /// (deterministic tests).
    /// </summary>
    public CastingService(
        IDlnaDiscoveryService discovery,
        IMediaHttpServer httpServer,
        IAvTransportClient avTransport,
        IRenderingControlClient renderingControl,
        TimeSpan? positionPollInterval = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _httpServer = httpServer ?? throw new ArgumentNullException(nameof(httpServer));
        _avTransport = avTransport ?? throw new ArgumentNullException(nameof(avTransport));
        _renderingControl = renderingControl ?? throw new ArgumentNullException(nameof(renderingControl));
        _pollInterval = positionPollInterval ?? DefaultPollInterval;
    }

    /// <inheritdoc />
    public CastingState State => _state;

    /// <inheritdoc />
    public DlnaDeviceInfo? CurrentDevice => _currentDevice;

    /// <inheritdoc />
    public string? ErrorMessage { get; private set; }

    /// <inheritdoc />
    public event EventHandler<CastingState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <inheritdoc />
    public event EventHandler? MediaEnded;

    /// <inheritdoc />
    public Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync() => _discovery.DiscoverDevicesAsync();

    /// <inheritdoc />
    public async Task StartCastingAsync(DlnaDeviceInfo device, string filePath, string title, TimeSpan duration = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                ErrorMessage = $"Arquivo de mídia não encontrado: {filePath}";
                SetState(CastingState.Error);
                throw new FileNotFoundException(ErrorMessage, filePath);
            }

            SetState(CastingState.Connecting);

            string? token = null;
            try
            {
                if (!_httpServer.IsRunning)
                {
                    await _httpServer.StartAsync().ConfigureAwait(false);
                }

                // 1. Extrai os metadados reais do arquivo de vídeo antes de registrar
                var videoInfo = GetVideoMetadata(filePath);

                videoInfo.Title = title;

                // 2. Determina o MIME Type correto (ex: video/mp4 ou video/x-matroska) baseado no arquivo
                string contentType = videoInfo.Extension == "mkv" ? "video/x-matroska" : $"video/{videoInfo.Extension}";

                // 3. Registra o arquivo no servidor HTTP com o ContentType preciso
                token = _httpServer.RegisterFile(filePath, contentType);
                videoInfo.Url = _httpServer.GetMediaUrl(token);

                // 4. Chama a nova versão do método passando o objeto completo de metadados
                var metadata = AvTransportClient.BuildDidlLiteMetadata(videoInfo);

                await _avTransport.SetAvTransportUriAsync(device, videoInfo.Url, metadata).ConfigureAwait(false);
                await _avTransport.PlayAsync(device).ConfigureAwait(false);

                _currentDevice = device;
                _currentToken = token;
                _currentDuration = duration > TimeSpan.Zero ? duration : videoInfo.Duration;
                _mediaEndedFired = false;
                ErrorMessage = null;
                SetState(CastingState.Streaming);
                StartPolling();
            }
            catch (Exception ex)
            {
                if (token is not null)
                {
                    _httpServer.UnregisterFile(token);
                }

                ErrorMessage = ex.Message;
                SetState(CastingState.Error);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Método para ler o arquivo fisicamente usando ffprobe
    private VideoMetadata GetVideoMetadata(string filePath)
    {
        try
        {
            var ffProbe = new NReco.VideoInfo.FFProbe();
            var info = ffProbe.GetMediaInfo(filePath);

            return new VideoMetadata
            {
                Width = info.Streams[0].Width,
                Fps = (int)Math.Round(info.Streams[0].FrameRate),
                VideoCodec = info.Streams[0].CodecName, // Retorna strings como "hevc", "h264"
                Extension = Path.GetExtension(filePath).Replace(".", "").ToLower(),
                Duration = info.Duration,
                Height = info.Streams[0].Height
            };
        }
        catch
        {
            // Fallback caso o ffprobe falhe em ler o arquivo (Gera configuração padrão segura)
            return new VideoMetadata
            {
                Width = 1920,
                Height = 1080,
                Fps = 30,
                VideoCodec = "h264",
                Extension = Path.GetExtension(filePath).Replace(".", "").ToLower(),
                Duration = TimeSpan.Zero,
            };
        }
    }

    /// <inheritdoc />
    public async Task PlayAsync()
    {
        var device = _currentDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            await _avTransport.PlayAsync(device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <inheritdoc />
    public async Task PauseAsync()
    {
        var device = _currentDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            await _avTransport.PauseAsync(device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <inheritdoc />
    public async Task StopCastingAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopPolling();

            var device = _currentDevice;
            if (device is not null)
            {
                try
                {
                    await _avTransport.StopAsync(device).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort: the renderer may already be gone; still clean up.
                }
            }

            if (_currentToken is not null)
            {
                _httpServer.UnregisterFile(_currentToken);
            }

            _currentDevice = null;
            _currentToken = null;
            ErrorMessage = null;
            SetState(CastingState.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SeekAsync(TimeSpan position)
    {
        var device = _currentDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            await _avTransport
                .SeekAsync(device, "REL_TIME", DlnaTime.Format(position))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <inheritdoc />
    public async Task SetVolumeAsync(int volume)
    {
        var device = _currentDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            await _renderingControl
                .SetVolumeAsync(device, "Master", Math.Clamp(volume, 0, 100))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <inheritdoc />
    public async Task<TimeSpan> GetPositionAsync()
    {
        var device = _currentDevice;
        if (device is null)
        {
            return TimeSpan.Zero;
        }

        var info = await _avTransport.GetPositionInfoAsync(device).ConfigureAwait(false);
        return info.RelTime;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPolling();
        _gate.Dispose();
    }

    /// <summary>
    /// Maps the file extension to the DLNA MIME type. The spec serves
    /// <c>video/mp4</c>; AVI sources are advertised as <c>video/avi</c> so
    /// renderers that sniff the MIME can still pick a decoder.
    /// </summary>
    internal static string ResolveContentType(string filePath)
        => string.Equals(Path.GetExtension(filePath), ".avi", StringComparison.OrdinalIgnoreCase)
            ? "video/avi"
            : "video/mp4";

    private void StartPolling()
    {
        StopPolling();

        if (_pollInterval <= TimeSpan.Zero)
        {
            return; // Disabled (tests).
        }

        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _ = Task.Run(() => PollLoopAsync(cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_pollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var device = _currentDevice;
                if (device is null)
                {
                    continue;
                }

                try
                {
                    var info = await _avTransport.GetPositionInfoAsync(device, cancellationToken).ConfigureAwait(false);
                    PositionChanged?.Invoke(this, info.RelTime);

                    // Detect end of episode: position >= duration - 2s threshold
                    if (!_mediaEndedFired && _currentDuration > TimeSpan.Zero)
                    {
                        var threshold = TimeSpan.FromSeconds(2);
                        if (info.RelTime >= _currentDuration - threshold)
                        {
                            _mediaEndedFired = true;
                            MediaEnded?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Transient renderer hiccup: keep polling, do not fail the session.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void StopPolling()
    {
        var cts = _pollCts;
        _pollCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void Fail(Exception exception)
    {
        ErrorMessage = exception.Message;
        SetState(CastingState.Error);
    }

    private void SetState(CastingState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }
}

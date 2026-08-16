using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using CATRA.Services.Casting;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Casting;

/// <summary>
/// Unit tests for the <see cref="CastingService"/> orchestrator (ST-08, RF-06)
/// using fakes for every dependency (discovery, HTTP server, AVTransport and
/// RenderingControl SOAP clients). Polling is disabled (<see cref="TimeSpan.Zero"/>)
/// for determinism. No SSDP/Kestrel/SOAP wire traffic, no renderer.
/// </summary>
public sealed class CastingServiceTests : IDisposable
{
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeHttpServer _httpServer = new();
    private readonly FakeAvTransport _avTransport = new();
    private readonly FakeRenderingControl _renderingControl = new();
    private readonly string _tempFile;

    private static readonly DlnaDeviceInfo Device = new()
    {
        FriendlyName = "Samsung TV",
        Udn = "uuid:1234",
        AvTransportControlUrl = "http://192.168.0.42/upnp/control/avt1",
        RenderingControlUrl = "http://192.168.0.42/upnp/control/rc1",
    };

    public CastingServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"catra-cast-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(_tempFile, new byte[1024]);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    private CastingService CreateService() =>
        new(_discovery, _httpServer, _avTransport, _renderingControl, TimeSpan.Zero);

    [Fact]
    public void InitialState_IsIdle_NoDevice()
    {
        using var sut = CreateService();

        sut.State.Should().Be(CastingState.Idle);
        sut.CurrentDevice.Should().BeNull();
        sut.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task StartCasting_HappyPath_TransitionsToStreaming_AndDrivesRenderer()
    {
        using var sut = CreateService();
        var states = new List<CastingState>();
        sut.StateChanged += (_, s) => states.Add(s);

        await sut.StartCastingAsync(Device, _tempFile, "Piloto");

        states.Should().ContainInOrder(CastingState.Connecting, CastingState.Streaming);
        sut.State.Should().Be(CastingState.Streaming);
        sut.CurrentDevice.Should().BeSameAs(Device);
        sut.ErrorMessage.Should().BeNull();

        _httpServer.RegisterCalls.Should().ContainSingle()
            .Which.Should().Be((_tempFile, "video/mp4"));
        _httpServer.StartCount.Should().Be(1); // server was not running
        _avTransport.SetUriCalls.Should().HaveCount(1);
        _avTransport.SetUriCalls[0].Uri.Should().Contain("/media/");
        _avTransport.SetUriCalls[0].Metadata.Should().Contain("Piloto");
        _avTransport.PlayCount.Should().Be(1);
    }

    [Fact]
    public async Task StartCasting_AviFile_RegistersAviContentType()
    {
        var avi = Path.Combine(Path.GetTempPath(), $"catra-cast-{Guid.NewGuid():N}.avi");
        await File.WriteAllBytesAsync(avi, new byte[16]);
        try
        {
            using var sut = CreateService();
            await sut.StartCastingAsync(Device, avi, "T");

            _httpServer.RegisterCalls.Should().ContainSingle()
                .Which.ContentType.Should().Be("video/avi");
        }
        finally
        {
            File.Delete(avi);
        }
    }

    [Fact]
    public async Task StartCasting_MissingFile_SetsErrorAndThrows()
    {
        using var sut = CreateService();
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.mp4");

        var act = () => sut.StartCastingAsync(Device, missing, "T");

        await act.Should().ThrowAsync<FileNotFoundException>();
        sut.State.Should().Be(CastingState.Error);
        sut.ErrorMessage.Should().Contain(missing);
        _httpServer.RegisterCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task StartCasting_RendererFails_SetsError_AndUnregistersToken()
    {
        _avTransport.ThrowOnSetUri = new InvalidOperationException("renderer gone");
        using var sut = CreateService();

        var act = () => sut.StartCastingAsync(Device, _tempFile, "T");

        await act.Should().ThrowAsync<InvalidOperationException>();
        sut.State.Should().Be(CastingState.Error);
        sut.ErrorMessage.Should().Be("renderer gone");
        _httpServer.RegisterCalls.Should().HaveCount(1);
        _httpServer.UnregisterCalls.Should().HaveCount(1); // rolled back
        _avTransport.PlayCount.Should().Be(0);
    }

    [Fact]
    public async Task StopCasting_StopsRenderer_Unregisters_AndGoesIdle()
    {
        using var sut = CreateService();
        await sut.StartCastingAsync(Device, _tempFile, "T");
        var registeredToken = _httpServer.LastToken;

        await sut.StopCastingAsync();

        sut.State.Should().Be(CastingState.Idle);
        sut.CurrentDevice.Should().BeNull();
        _avTransport.StopCount.Should().Be(1);
        _httpServer.UnregisterCalls.Should().Contain(registeredToken);
        _httpServer.StopCount.Should().Be(0); // server keeps running per spec
    }

    [Fact]
    public async Task TransportCommands_ForwardToCurrentDevice_OnlyWhenStreaming()
    {
        using var sut = CreateService();

        // Idle: no device → no-ops.
        await sut.PlayAsync();
        await sut.PauseAsync();
        await sut.SeekAsync(TimeSpan.FromSeconds(30));
        await sut.SetVolumeAsync(50);
        _avTransport.PlayCount.Should().Be(0);
        _avTransport.PauseCount.Should().Be(0);
        _avTransport.SeekCalls.Should().BeEmpty();
        _renderingControl.VolumeCalls.Should().BeEmpty();

        await sut.StartCastingAsync(Device, _tempFile, "T");

        await sut.PlayAsync();
        await sut.PauseAsync();
        await sut.SeekAsync(TimeSpan.FromMinutes(1));
        await sut.SetVolumeAsync(250); // clamped to 100

        _avTransport.PlayCount.Should().Be(2); // +1 from StartCasting
        _avTransport.PauseCount.Should().Be(1);
        _avTransport.SeekCalls.Should().ContainSingle()
            .Which.Should().Be(("REL_TIME", DlnaTime.Format(TimeSpan.FromMinutes(1))));
        _renderingControl.VolumeCalls.Should().ContainSingle()
            .Which.Should().Be(("Master", 100));
    }

    [Fact]
    public async Task GetPosition_ReturnsRendererRelTime_OrZeroWhenIdle()
    {
        using var sut = CreateService();
        (await sut.GetPositionAsync()).Should().Be(TimeSpan.Zero);

        _avTransport.PositionToReturn = new PositionInfo(TimeSpan.FromSeconds(125), TimeSpan.FromMinutes(22));
        await sut.StartCastingAsync(Device, _tempFile, "T");

        (await sut.GetPositionAsync()).Should().Be(TimeSpan.FromSeconds(125));
    }

    [Fact]
    public async Task DiscoverDevices_DelegatesToDiscovery()
    {
        _discovery.DevicesToReturn.Add(Device);
        using var sut = CreateService();

        var result = await sut.DiscoverDevicesAsync();

        result.Should().ContainSingle().Which.Should().BeSameAs(Device);
        _discovery.DiscoverCount.Should().Be(1);
    }

    [Fact]
    public async Task PlayFailure_SetsErrorState()
    {
        using var sut = CreateService();
        await sut.StartCastingAsync(Device, _tempFile, "T");
        _avTransport.ThrowOnPlay = new InvalidOperationException("boom");

        await sut.PlayAsync(); // swallowed, but recorded as Error

        sut.State.Should().Be(CastingState.Error);
        sut.ErrorMessage.Should().Be("boom");
    }

    // ------------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------------

    private sealed class FakeDiscovery : IDlnaDiscoveryService
    {
        public List<DlnaDeviceInfo> DevicesToReturn { get; } = new();
        public int DiscoverCount { get; private set; }
        public event EventHandler<IReadOnlyList<DlnaDeviceInfo>>? DevicesFound;

        public Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync(
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            DevicesFound?.Invoke(this, DevicesToReturn);
            return Task.FromResult(DevicesToReturn.ToList());
        }
    }

    private sealed class FakeHttpServer : IMediaHttpServer
    {
        private int _tokenCounter;
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public List<(string FilePath, string ContentType)> RegisterCalls { get; } = new();
        public List<string> UnregisterCalls { get; } = new();
        public string LastToken { get; private set; } = string.Empty;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            IsRunning = true;
            Port = 8060;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            IsRunning = false;
            Port = 0;
            return Task.CompletedTask;
        }

        public string RegisterFile(string filePath, string contentType)
        {
            RegisterCalls.Add((filePath, contentType));
            LastToken = $"token-{++_tokenCounter}";
            return LastToken;
        }

        public bool UnregisterFile(string token)
        {
            UnregisterCalls.Add(token);
            return true;
        }

        public string GetMediaUrl(string token) => $"http://192.168.0.10:{Port}/media/{token}";

        public string GetLocalIp() => "192.168.0.10";
    }

    private sealed class FakeAvTransport : IAvTransportClient
    {
        public Exception? ThrowOnSetUri { get; set; }
        public Exception? ThrowOnPlay { get; set; }
        public List<(string Uri, string Metadata)> SetUriCalls { get; } = new();
        public int PlayCount { get; private set; }
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }
        public List<(string Unit, string Target)> SeekCalls { get; } = new();
        public PositionInfo PositionToReturn { get; set; } = new(TimeSpan.Zero, TimeSpan.Zero);

        public Task SetAvTransportUriAsync(
            DlnaDeviceInfo device, string uri, string didlLiteMetadata, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSetUri is not null)
            {
                throw ThrowOnSetUri;
            }

            SetUriCalls.Add((uri, didlLiteMetadata));
            return Task.CompletedTask;
        }

        public Task PlayAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPlay is not null)
            {
                throw ThrowOnPlay;
            }

            PlayCount++;
            return Task.CompletedTask;
        }

        public Task PauseAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
        {
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task SeekAsync(DlnaDeviceInfo device, string unit, string target, CancellationToken cancellationToken = default)
        {
            SeekCalls.Add((unit, target));
            return Task.CompletedTask;
        }

        public Task<PositionInfo> GetPositionInfoAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.FromResult(PositionToReturn);

        public Task<string> GetTransportStateAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.FromResult("PLAYING");

        public Task NextAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PreviousAsync(DlnaDeviceInfo device, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeRenderingControl : IRenderingControlClient
    {
        public List<(string Channel, int Volume)> VolumeCalls { get; } = new();

        public Task SetVolumeAsync(DlnaDeviceInfo device, string channel, int volume, CancellationToken cancellationToken = default)
        {
            VolumeCalls.Add((channel, volume));
            return Task.CompletedTask;
        }

        public Task<int> GetVolumeAsync(DlnaDeviceInfo device, string channel, CancellationToken cancellationToken = default)
            => Task.FromResult(20);
    }
}

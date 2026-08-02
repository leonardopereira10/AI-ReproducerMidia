using System.Net;
using System.Net.Http.Headers;
using CATRA.Services.Casting;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Casting;

/// <summary>
/// Integration tests for <see cref="MediaHttpServer"/> (ST-08, RF-06) against a
/// REAL embedded Kestrel bound to <c>127.0.0.1</c> on an ephemeral port. A real
/// 10KB temp file with a deterministic byte pattern is registered and fetched
/// over <see cref="HttpClient"/> to validate whole-file, partial (206) and
/// open-ended range responses plus token-based 404s. No multicast / renderer.
/// </summary>
public sealed class MediaHttpServerTests : IAsyncLifetime
{
    private const int FileSize = 10 * 1024; // 10KB
    private const string ContentType = "video/mp4";

    private readonly byte[] _payload = new byte[FileSize];
    private string _tempFile = string.Empty;
    private MediaHttpServer _server = null!;
    private HttpClient _client = null!;
    private string _token = string.Empty;
    private string _baseUrl = string.Empty;

    public async Task InitializeAsync()
    {
        // Deterministic pattern so range slices can be asserted byte-for-byte.
        for (var i = 0; i < FileSize; i++)
        {
            _payload[i] = (byte)(i % 251);
        }

        _tempFile = Path.Combine(Path.GetTempPath(), $"catra-dlna-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(_tempFile, _payload);

        // Bind loopback + ephemeral port: LAN code path is identical, no firewall.
        _server = new MediaHttpServer("127.0.0.1", 0);
        await _server.StartAsync();

        _token = _server.RegisterFile(_tempFile, ContentType);
        _baseUrl = $"http://127.0.0.1:{_server.Port}/media/{_token}";
        _client = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();

        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public async Task Start_BindsEphemeralPort_AndReportsRunning()
    {
        _server.IsRunning.Should().BeTrue();
        _server.Port.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_FullFile_Returns200_WithRangeHeadersAndExactBytes()
    {
        using var response = await _client.GetAsync(_baseUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ContentType);
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Content.Headers.ContentLength.Should().Be(FileSize);

        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().Equal(_payload);
    }

    [Fact]
    public async Task Get_ClosedRange_Returns206_WithContentRangeAndSlice()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl);
        request.Headers.Range = new RangeHeaderValue(0, 99);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentLength.Should().Be(100);
        response.Content.Headers.ContentRange!.ToString()
            .Should().Be($"bytes 0-99/{FileSize}");

        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().Equal(_payload.Take(100));
    }

    [Fact]
    public async Task Get_OpenEndedRange_Returns206_UntilEndOfFile()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl);
        request.Headers.Range = new RangeHeaderValue(5000, null);

        using var response = await _client.SendAsync(request);

        var expectedCount = FileSize - 5000;
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentLength.Should().Be(expectedCount);
        response.Content.Headers.ContentRange!.ToString()
            .Should().Be($"bytes 5000-{FileSize - 1}/{FileSize}");

        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().Equal(_payload.Skip(5000));
    }

    [Fact]
    public async Task Get_UnknownToken_Returns404()
    {
        var url = $"http://127.0.0.1:{_server.Port}/media/{Guid.NewGuid():N}";

        using var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_TraversalToken_Returns404()
    {
        var url = $"http://127.0.0.1:{_server.Port}/media/..%2F..%2Fsecret";

        using var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unregister_TokenMakesIt404_AndReturnsFalseSecondTime()
    {
        var token = _server.RegisterFile(_tempFile, ContentType);
        var url = $"http://127.0.0.1:{_server.Port}/media/{token}";

        _server.UnregisterFile(token).Should().BeTrue();
        _server.UnregisterFile(token).Should().BeFalse();

        using var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dispose_StopsServer_AndClearsRegistrations()
    {
        var server = new MediaHttpServer("127.0.0.1", 0);
        await server.StartAsync();
        var token = server.RegisterFile(_tempFile, ContentType);
        server.IsRunning.Should().BeTrue();

        await server.DisposeAsync();

        server.IsRunning.Should().BeFalse();
        server.Port.Should().Be(0);
        server.UnregisterFile(token).Should().BeFalse(); // registrations cleared
    }
}

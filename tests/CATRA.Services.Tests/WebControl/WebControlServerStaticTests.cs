using System.Net;
using CATRA.Services.WebControl;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.WebControl;

/// <summary>
/// Integration smoke tests for <see cref="WebControlServer"/> static assets:
/// the panel must be fully self-contained on the LAN (no CDN), so the
/// Font Awesome subset (CSS + woff2) is served from embedded resources.
/// </summary>
public sealed class WebControlServerStaticTests : IAsyncLifetime
{
    private WebControlServer _server = null!;
    private HttpClient _client = null!;
    private string _baseUrl = string.Empty;

    public async Task InitializeAsync()
    {
        // Ephemeral port; no services wired — static routes must work standalone.
        _server = new WebControlServer(port: 0);
        await _server.StartAsync();
        _baseUrl = $"http://127.0.0.1:{_server.Port}";
        _client = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Index_ReferencesFontAwesomeStylesheet()
    {
        var response = await _client.GetAsync($"{_baseUrl}/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("/css/fontawesome.css");
        html.Should().NotContain("⏮").And.NotContain("⏭").And.NotContain("📺");
    }

    [Fact]
    public async Task FontAwesomeCss_ServedFromEmbeddedResources()
    {
        var response = await _client.GetAsync($"{_baseUrl}/css/fontawesome.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
        var css = await response.Content.ReadAsStringAsync();
        css.Should().Contain("@font-face");
        css.Should().Contain("fa_solid_900.woff2");
        css.Should().Contain(".fa-play");
    }

    [Fact]
    public async Task FontAwesomeFont_ServedWithWoff2MagicBytes()
    {
        var response = await _client.GetAsync($"{_baseUrl}/webfonts/fa_solid_900.woff2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("font/woff2");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(100_000);
        bytes[0..4].Should().Equal((byte)'w', (byte)'O', (byte)'F', (byte)'2');
    }

    [Fact]
    public async Task UnknownStaticPath_Returns404()
    {
        var response = await _client.GetAsync($"{_baseUrl}/webfonts/does-not-exist.woff2");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

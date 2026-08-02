using CATRA.Services.Casting;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Casting;

/// <summary>
/// Unit tests for <see cref="DlnaDescriptionParser"/> (ST-08, RF-06): UPnP
/// device-description XML → <see cref="CATRA.Core.Models.DlnaDeviceInfo"/>.
/// Pure parsing only — no UDP/SSDP/multicast. Covers friendlyName/UDN extraction,
/// relative + absolute controlURL resolution, the AVTransport-only filter and
/// malformed-input tolerance.
/// </summary>
public sealed class DlnaDiscoveryTests
{
    private const string Location = "http://192.168.0.42:9197/desc.xml";

    private const string FullDescription =
        "<?xml version=\"1.0\"?>" +
        "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">" +
        "  <device>" +
        "    <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>" +
        "    <friendlyName>Samsung TV [Living Room]</friendlyName>" +
        "    <manufacturer>Samsung Electronics</manufacturer>" +
        "    <modelName>UN55TU7000</modelName>" +
        "    <UDN>uuid:068e7781-0099-1000-8000-d60000000000</UDN>" +
        "    <serviceList>" +
        "      <service>" +
        "        <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>" +
        "        <controlURL>/upnp/control/avt1</controlURL>" +
        "      </service>" +
        "      <service>" +
        "        <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>" +
        "        <controlURL>/upnp/control/rc1</controlURL>" +
        "      </service>" +
        "      <service>" +
        "        <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>" +
        "        <controlURL>/upnp/control/cm1</controlURL>" +
        "      </service>" +
        "    </serviceList>" +
        "  </device>" +
        "</root>";

    [Fact]
    public void Parse_FullDescription_ExtractsIdentityAndControlUrls()
    {
        var info = DlnaDescriptionParser.Parse(FullDescription, Location);

        info.Should().NotBeNull();
        info!.FriendlyName.Should().Be("Samsung TV [Living Room]");
        info.Udn.Should().Be("uuid:068e7781-0099-1000-8000-d60000000000");
        info.Manufacturer.Should().Be("Samsung Electronics");
        info.ModelName.Should().Be("UN55TU7000");
        info.DescriptionUrl.Should().Be(Location);
        // Relative controlURLs resolved against the LOCATION scheme/host/port.
        info.AvTransportControlUrl.Should().Be("http://192.168.0.42:9197/upnp/control/avt1");
        info.RenderingControlUrl.Should().Be("http://192.168.0.42:9197/upnp/control/rc1");
    }

    [Fact]
    public void Parse_AbsoluteControlUrls_PassThroughUnchanged()
    {
        const string xml =
            "<root><device>" +
            "<friendlyName>TV</friendlyName>" +
            "<serviceList>" +
            "<service>" +
            "<serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>" +
            "<controlURL>http://10.0.0.9:1234/ctrl/avt</controlURL>" +
            "</service>" +
            "</serviceList>" +
            "</device></root>";

        var info = DlnaDescriptionParser.Parse(xml, Location);

        info.Should().NotBeNull();
        info!.AvTransportControlUrl.Should().Be("http://10.0.0.9:1234/ctrl/avt");
        info.RenderingControlUrl.Should().BeNull(); // not advertised
    }

    [Fact]
    public void Parse_WithoutAvTransport_ReturnsNull_DiscoveryFilter()
    {
        const string xml =
            "<root><device>" +
            "<friendlyName>Speaker</friendlyName>" +
            "<serviceList>" +
            "<service>" +
            "<serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>" +
            "<controlURL>/rc</controlURL>" +
            "</service>" +
            "</serviceList>" +
            "</device></root>";

        DlnaDescriptionParser.Parse(xml, Location).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<root><device><friendlyName>X</friendlyName></device></root>")] // no serviceList
    [InlineData("this is not xml at all")]
    [InlineData("<root><device>")] // malformed / unterminated
    public void Parse_MalformedOrNoAvTransport_ReturnsNull(string xml)
        => DlnaDescriptionParser.Parse(xml, Location).Should().BeNull();

    [Fact]
    public void Parse_IsNamespaceAgnostic_MatchesOnLocalName()
    {
        // Samsung-style prefixed namespaces: parser matches LocalName, not NS.
        const string xml =
            "<d:root xmlns:d=\"urn:schemas-upnp-org:device-1-0\">" +
            "<d:device>" +
            "<d:friendlyName>NS TV</d:friendlyName>" +
            "<d:serviceList>" +
            "<d:service>" +
            "<d:serviceType>urn:schemas-upnp-org:service:AVTransport:1</d:serviceType>" +
            "<d:controlURL>/avt</d:controlURL>" +
            "</d:service>" +
            "</d:serviceList>" +
            "</d:device>" +
            "</d:root>";

        var info = DlnaDescriptionParser.Parse(xml, Location);

        info.Should().NotBeNull();
        info!.FriendlyName.Should().Be("NS TV");
        info.AvTransportControlUrl.Should().Be("http://192.168.0.42:9197/avt");
    }

    [Theory]
    [InlineData("/upnp/control/avt1", "http://192.168.0.42:9197/upnp/control/avt1")]
    [InlineData("http://1.2.3.4:8080/x", "http://1.2.3.4:8080/x")]
    public void ResolveUrl_RelativeAndAbsolute(string controlUrl, string expected)
        => DlnaDescriptionParser.ResolveUrl(Location, controlUrl).Should().Be(expected);
}

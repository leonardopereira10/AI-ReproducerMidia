using System.Text;

namespace CATRA.Services.Casting;

/// <summary>
/// Builds the SSDP <c>M-SEARCH</c> datagram (ST-08). Pure/static so the wire
/// format is unit-testable without touching a socket.
/// </summary>
public static class SsdpSearchMessage
{
    /// <summary>SSDP multicast address (IPv4).</summary>
    public const string MulticastAddress = "239.255.255.250";

    /// <summary>SSDP multicast port.</summary>
    public const int MulticastPort = 1900;

    /// <summary>
    /// Builds the M-SEARCH request for <paramref name="searchTarget"/>
    /// (<c>MX</c> defaults to 3s). Terminated by the mandatory blank line.
    /// </summary>
    public static string Build(string searchTarget, int mxSeconds = 3)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchTarget);
        if (mxSeconds < 1)
        {
            mxSeconds = 1;
        }

        return new StringBuilder()
            .Append("M-SEARCH * HTTP/1.1\r\n")
            .Append("HOST: ").Append(MulticastAddress).Append(':').Append(MulticastPort).Append("\r\n")
            .Append("MAN: \"ssdp:discover\"\r\n")
            .Append("MX: ").Append(mxSeconds).Append("\r\n")
            .Append("ST: ").Append(searchTarget).Append("\r\n")
            .Append("\r\n")
            .ToString();
    }

    /// <summary>UTF-8 bytes of <see cref="Build(string, int)"/>.</summary>
    public static byte[] BuildBytes(string searchTarget, int mxSeconds = 3)
        => Encoding.UTF8.GetBytes(Build(searchTarget, mxSeconds));
}

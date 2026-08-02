namespace CATRA.Services.Casting;

/// <summary>
/// Parses raw SSDP datagrams into <see cref="SsdpResponse"/> (ST-08).
/// Pure/static: header extraction is unit-testable without multicast I/O.
/// </summary>
public static class SsdpResponseParser
{
    /// <summary>
    /// Parses one datagram. Returns <c>null</c> when it is not a valid
    /// <c>HTTP/1.1 200 OK</c> reply or has no <c>LOCATION</c> header.
    /// </summary>
    public static SsdpResponse? Parse(string datagram)
    {
        if (string.IsNullOrWhiteSpace(datagram))
        {
            return null;
        }

        var lines = datagram.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            return null; // NOTIFY / garbage: only M-SEARCH replies are interesting.
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            int sep = line.IndexOf(':');
            if (sep <= 0)
            {
                continue;
            }

            var name = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            headers[name] = value;
        }

        if (!headers.TryGetValue("LOCATION", out var location) || string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        headers.TryGetValue("ST", out var st);
        headers.TryGetValue("USN", out var usn);
        return new SsdpResponse(location, st, usn);
    }
}

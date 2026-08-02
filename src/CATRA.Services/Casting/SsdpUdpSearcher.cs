using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CATRA.Services.Casting;

/// <summary>
/// Raw-UDP SSDP M-SEARCH implementation (ST-08). Chosen over the Rssdp NuGet
/// package: the whole protocol surface needed here is one multicast datagram +
/// header parsing (~60 lines), keeping CATRA dependency-free and fully
/// controllable/testable. Sends to 239.255.255.250:1900 and collects replies
/// until the timeout.
/// </summary>
public sealed class SsdpUdpSearcher : ISsdpSearcher
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SsdpResponse>> SearchAsync(
        string searchTarget,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var responses = new List<SsdpResponse>();

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var payload = SsdpSearchMessage.BuildBytes(searchTarget);
        var multicast = new IPEndPoint(IPAddress.Parse(SsdpSearchMessage.MulticastAddress), SsdpSearchMessage.MulticastPort);
        await udp.SendAsync(payload, payload.Length, multicast).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                var parsed = SsdpResponseParser.Parse(Encoding.UTF8.GetString(result.Buffer));
                if (parsed is null)
                {
                    continue;
                }

                // Accept replies whose ST matches the search target (or omit ST —
                // some renderers do; the description fetch filters definitively).
                if (parsed.SearchTarget is null ||
                    parsed.SearchTarget.Contains(searchTarget, StringComparison.OrdinalIgnoreCase))
                {
                    responses.Add(parsed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the collection window elapsed (or the caller cancelled).
        }
        catch (SocketException)
        {
            // Network unreachable / no adapter: report what we have (nothing).
        }

        return responses;
    }
}

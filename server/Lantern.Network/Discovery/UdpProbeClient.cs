using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lantern.Network.Discovery;

using Lantern.Core.Discovery;
using Lantern.Core.Dtos;

/// <summary>
/// One-shot client probe: sends a <see cref="DiscoveryKinds.Probe"/> from a private ephemeral
/// port and collects the unicast replies. This is what the desktop "search now" button and the
/// Android connect screen use – it resolves in a few hundred milliseconds instead of waiting for
/// the next announcement, and it works even when the client cannot bind the shared beacon port.
/// </summary>
public sealed class UdpProbeClient
{
    private readonly ILogger _log;

    public UdpProbeClient(ILogger<UdpProbeClient>? logger = null) =>
        _log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Beacon port of the servers being probed.</summary>
    public int Port { get; init; } = DiscoveryConstants.BeaconPort;

    /// <summary>Identifies this device in the probe so the server can log who asked.</summary>
    public string? ClientId { get; init; }

    public string? ClientName { get; init; }

    public int Attempts { get; init; } = 2;

    /// <summary>Sends the probe and returns every distinct server that answered.</summary>
    public async Task<IReadOnlyList<DiscoveryServerDto>> ProbeAsync(
        TimeSpan wait,
        CancellationToken ct = default)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.EnableBroadcast = true;
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Could not open a discovery probe socket. {ex.Message}", ex);
        }

        var probeId = Guid.NewGuid().ToString("N");
        var packet = new DiscoveryPacket
        {
            Kind = DiscoveryKinds.Probe,
            ProbeId = probeId,
            ClientId = ClientId,
            ClientName = ClientName,
        };

        var payload = BeaconCodec.Encode(packet);
        foreach (var target in Targets())
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                Send(socket, payload, target);
            }
        }

        var found = new Dictionary<string, DiscoveryServerDto>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + wait;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(deadline - DateTime.UtcNow);
                try
                {
                    received = await socket.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex)
                {
                    _log.LogDebug(ex, "Probe: receive failed");
                    break;
                }
                catch (ObjectDisposedException) { break; }
            }

            if (!BeaconCodec.TryDecode(received.Buffer, out var reply) || reply is null) continue;
            if (reply.IsProbe) continue;

            // Only trust replies that echo our probe id; stale/broadcast packets are ignored.
            if (!string.Equals(reply.ProbeId, probeId, StringComparison.Ordinal)) continue;
            if (reply.Server is not { } server || string.IsNullOrWhiteSpace(server.ServerId)) continue;

            found[server.ServerId] = server;
        }

        if (found.Count == 0)
        {
            _log.LogInformation("Probe: no server answered within {Wait}", wait);
        }

        return [.. found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private IEnumerable<IPEndPoint> Targets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, Port);
        yield return new IPEndPoint(IPAddress.Parse(DiscoveryConstants.BeaconMulticastGroup), Port);
    }

    private void Send(UdpClient socket, byte[] payload, IPEndPoint target)
    {
        try
        {
            socket.Send(payload, payload.Length, target);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "Probe: send to {Target} failed", target);
        }
        catch (ObjectDisposedException) { }
    }
}

using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lantern.Network.Discovery;

using Lantern.Core.Discovery;
using Lantern.Core.Dtos;

/// <summary>
/// Server side of the fallback discovery channel: repeats an <see cref="DiscoveryKinds.Announce"/>
/// packet to broadcast and multicast on <see cref="DiscoveryConstants.BeaconPort"/> and answers
/// probes unicast. mDNS is the primary mechanism (Android's NsdManager prefers it); this exists
/// because plenty of consumer routers filter multicast between Wi-Fi clients.
/// </summary>
public sealed class UdpBeaconBroadcaster : IAsyncDisposable
{
    private readonly Func<DiscoveryServerDto> _serverProvider;
    private readonly ILogger _log;
    private readonly object _gate = new();

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _packetsSent;
    private long _goodbyesSent;
    private long _probesAnswered;

    public UdpBeaconBroadcaster(Func<DiscoveryServerDto> serverProvider, ILogger<UdpBeaconBroadcaster>? logger = null)
    {
        _serverProvider = serverProvider;
        _log = (ILogger?)logger ?? NullLogger.Instance;
    }

    public UdpBeaconBroadcaster(DiscoveryServerDto server, ILogger<UdpBeaconBroadcaster>? logger = null)
        : this(() => server, logger)
    {
    }

    public int Port { get; init; } = DiscoveryConstants.BeaconPort;

    /// <summary>How often the announcement is repeated. Clients expire a server after 12s.</summary>
    public TimeSpan AnnounceInterval { get; init; } = DiscoveryConstants.BeaconInterval;

    /// <summary>Send to 255.255.255.255 (works on almost every home LAN).</summary>
    public bool UseBroadcast { get; init; } = true;

    /// <summary>Also send to the Lantern multicast group (survives broadcast filtering).</summary>
    public bool UseMulticast { get; init; } = true;

    /// <summary>Send a copy to 127.0.0.1 so a co-located diagnostic listener sees the traffic.</summary>
    public bool UseLoopback { get; init; } = true;

    /// <summary>Multicast hop limit; 1 keeps the beacons on this LAN.</summary>
    public int MulticastTtl { get; init; } = 1;

    public bool AnnounceOnStart { get; init; } = true;

    public bool SendGoodbyeOnStop { get; init; } = true;

    public bool IsRunning => _socket is not null;

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    public long GoodbyesSent => Interlocked.Read(ref _goodbyesSent);

    public long ProbesAnswered => Interlocked.Read(ref _probesAnswered);

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_socket is not null) return Task.CompletedTask;

        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.EnableBroadcast = true;
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, MulticastTtl);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException($"Could not open the discovery beacon socket. {ex.Message}", ex);
        }

        _socket = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AnnounceLoopAsync(_cts.Token));

        _log.LogInformation("UDP beacon broadcasting on port {Port} every {Interval}", Port, AnnounceInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        UdpClient? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is null) return;

        _cts?.Cancel();
        if (SendGoodbyeOnStop)
        {
            // Tell listening clients to drop us now instead of waiting for their timeout.
            SendToAll(BuildPacket(DiscoveryKinds.Goodbye), socket);
            Interlocked.Increment(ref _goodbyesSent);
        }

        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            socket.Dispose();
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Sends one announcement right now (e.g. after the user renames the server).</summary>
    public void Announce()
    {
        var socket = _socket;
        if (socket is null) return;

        SendToAll(BuildPacket(DiscoveryKinds.Announce), socket);
        Interlocked.Increment(ref _packetsSent);
    }

    /// <summary>Answers a client probe with a unicast announcement that echoes its probe id.</summary>
    public void ReplyToProbe(DiscoveryPacket probe, IPEndPoint remote)
    {
        var socket = _socket;
        if (socket is null || remote.Address is null) return;

        Send(DiscoveryPacket.Reply(_serverProvider(), probe.ProbeId), socket, remote);
        Interlocked.Increment(ref _probesAnswered);
    }

    private DiscoveryPacket BuildPacket(string kind) => new()
    {
        Kind = kind,
        Server = _serverProvider(),
    };

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            // Two quick announcements so a client that starts listening a moment after the
            // server still catches one even if the first datagram is lost to a sleeping radio.
            if (AnnounceOnStart)
            {
                Announce();
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                Announce();
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(AnnounceInterval, ct).ConfigureAwait(false);
                Announce();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SendToAll(DiscoveryPacket packet, UdpClient socket)
    {
        var payload = BeaconCodec.Encode(packet);

        if (UseBroadcast) SendRaw(payload, socket, new IPEndPoint(IPAddress.Broadcast, Port));
        if (UseMulticast) SendRaw(payload, socket, new IPEndPoint(IPAddress.Parse(DiscoveryConstants.BeaconMulticastGroup), Port));
        if (UseLoopback) SendRaw(payload, socket, new IPEndPoint(IPAddress.Loopback, Port));
    }

    private void Send(DiscoveryPacket packet, UdpClient socket, IPEndPoint target) =>
        SendRaw(BeaconCodec.Encode(packet), socket, target);

    private void SendRaw(byte[] payload, UdpClient socket, IPEndPoint target)
    {
        try
        {
            socket.Send(payload, payload.Length, target);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "Beacon: send to {Target} failed", target);
        }
        catch (ObjectDisposedException) { }
    }
}

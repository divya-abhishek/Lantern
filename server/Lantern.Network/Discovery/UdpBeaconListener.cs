using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lantern.Network.Discovery;

using Lantern.Core.Discovery;
using Lantern.Core.Dtos;

/// <summary>
/// Client side of the fallback discovery channel: listens on
/// <see cref="DiscoveryConstants.BeaconPort"/> for announcements, keeps a live server list and
/// can send a probe when the client needs an answer immediately. Also usable on the PC as a
/// self-check ("is my beacon actually leaving this machine?").
/// </summary>
public sealed class UdpBeaconListener : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DiscoveryServerDto Server, DateTime LastSeenUtc)> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _outstandingProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPAddress> _joined = [];

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _pruneLoop;
    private long _packetsReceived;
    private long _announcementsReceived;
    private long _probesReceived;
    private long _probesSent;
    private long _probeReplies;
    private long _foreignPackets;

    public UdpBeaconListener(ILogger<UdpBeaconListener>? logger = null) =>
        _log = (ILogger?)logger ?? NullLogger.Instance;

    public int Port { get; init; } = DiscoveryConstants.BeaconPort;

    /// <summary>Servers that stay silent for this long are reported as gone.</summary>
    public TimeSpan ServerTimeout { get; init; } = DiscoveryConstants.ServerTimeout;

    /// <summary>Join the beacon multicast group in addition to the broadcast address.</summary>
    public bool JoinMulticast { get; init; } = true;

    /// <summary>Identifies this device in probes so the server can log who asked.</summary>
    public string? ClientId { get; init; }

    public string? ClientName { get; init; }

    /// <summary>Server role: answer incoming probes (see <see cref="ServerProvider"/>).</summary>
    public bool AnswerProbes { get; init; }

    /// <summary>Supplies the payload used to answer probes; required when <see cref="AnswerProbes"/> is set.</summary>
    public Func<DiscoveryServerDto>? ServerProvider { get; init; }

    /// <summary>Sends a probe as soon as <see cref="StartAsync"/> runs.</summary>
    public bool ProbeOnStart { get; init; } = true;

    public bool IsRunning => _socket is not null;

    public IReadOnlyList<IPAddress> JoinedInterfaces => _joined;

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    public long AnnouncementsReceived => Interlocked.Read(ref _announcementsReceived);

    public long ProbesReceived => Interlocked.Read(ref _probesReceived);

    public long ProbeRepliesReceived => Interlocked.Read(ref _probeReplies);

    public long ProbesSent => Interlocked.Read(ref _probesSent);

    /// <summary>Datagrams on our port that were not Lantern discovery packets.</summary>
    public long ForeignPackets => Interlocked.Read(ref _foreignPackets);

    public IReadOnlyList<DiscoveryServerDto> Servers
    {
        get
        {
            lock (_gate) return [.. _servers.Values.Select(v => v.Server).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Raised for every announce/goodbye that (re)confirms a server.</summary>
    public event EventHandler<DiscoveryServerDto>? ServerAnnounced;

    /// <summary>Raised when a server sends a goodbye or stops being heard from.</summary>
    public event EventHandler<string>? ServerGone;

    public event EventHandler<(DiscoveryPacket Packet, IPEndPoint Remote)>? ProbeReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_socket is not null) return Task.CompletedTask;
        if (AnswerProbes && ServerProvider is null)
        {
            throw new InvalidOperationException($"{nameof(AnswerProbes)} requires {nameof(ServerProvider)}.");
        }

        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.EnableBroadcast = true;
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            if (JoinMulticast)
            {
                var group = IPAddress.Parse(DiscoveryConstants.BeaconMulticastGroup);
                foreach (var address in LanAddressResolver.GetLanAddresses())
                {
                    try
                    {
                        socket.JoinMulticastGroup(group, address);
                        _joined.Add(address);
                    }
                    catch (SocketException ex)
                    {
                        _log.LogDebug(ex, "Beacon: could not join {Group} on {Address}", group, address);
                    }
                }
            }
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException($"Could not listen for discovery beacons on UDP port {Port}. {ex.Message}", ex);
        }

        _socket = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _pruneLoop = Task.Run(() => PruneLoopAsync(_cts.Token));

        _log.LogInformation("UDP beacon listener active on port {Port} (multicast: {Multicast})", Port, _joined.Count > 0);
        if (ProbeOnStart) SendProbe();

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
        try
        {
            foreach (var task in new[] { _receiveLoop, _pruneLoop })
            {
                if (task is not null) await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            socket.Dispose();
            _cts?.Dispose();
            _cts = null;
            _receiveLoop = null;
            _pruneLoop = null;
            _joined.Clear();
            lock (_gate)
            {
                _servers.Clear();
                _outstandingProbes.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Sends a probe from the listening socket. Replies are unicast back to this socket, so the
    /// server list updates through <see cref="ServerAnnounced"/>. See
    /// <see cref="UdpProbeClient"/> for the one-shot variant used by "find server now".
    /// </summary>
    public void SendProbe()
    {
        var socket = _socket;
        if (socket is null) return;

        var packet = NewProbe(out var probeId);
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            // Keep the bookkeeping bounded: a probe that has not been answered is stale by now.
            foreach (var stale in _outstandingProbes.Where(kv => now - kv.Value > ServerTimeout).Select(kv => kv.Key).ToArray())
            {
                _outstandingProbes.Remove(stale);
            }

            _outstandingProbes[probeId] = now;
        }

        var payload = BeaconCodec.Encode(packet);
        foreach (var target in ProbeTargets()) SendRaw(payload, socket, target);

        Interlocked.Increment(ref _probesSent);
    }

    private DiscoveryPacket NewProbe(out string probeId)
    {
        probeId = Guid.NewGuid().ToString("N");
        return new DiscoveryPacket
        {
            Kind = DiscoveryKinds.Probe,
            ProbeId = probeId,
            ClientId = ClientId,
            ClientName = ClientName,
        };
    }

    private IEnumerable<IPEndPoint> ProbeTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, Port);
        yield return new IPEndPoint(IPAddress.Parse(DiscoveryConstants.BeaconMulticastGroup), Port);
    }

    private bool SendRaw(byte[] payload, UdpClient socket, IPEndPoint target)
    {
        try
        {
            socket.Send(payload, payload.Length, target);
            return true;
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "Beacon: send to {Target} failed", target);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null) return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "Beacon: receive error, continuing");
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            Interlocked.Increment(ref _packetsReceived);

            if (!BeaconCodec.TryDecode(received.Buffer, out var packet) || packet is null)
            {
                Interlocked.Increment(ref _foreignPackets);
                continue;
            }

            try
            {
                Handle(packet, received.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Beacon: unexpected error while handling a packet");
            }
        }
    }

    public event EventHandler<DiscoveryReceiverDto>? ReceiverAnnounced;

    private void Handle(DiscoveryPacket packet, IPEndPoint remote)
    {
        if (packet.IsProbe)
        {
            Interlocked.Increment(ref _probesReceived);
            ProbeReceived?.Invoke(this, (packet, remote));

            // The reply is unicast to the envelope the probe came from: the prober may have used
            // its listening port (the normal case) or a private ephemeral port (one-shot probe).
            var provider = ServerProvider;
            if (AnswerProbes && provider is not null && remote.Address is not null)
            {
                var socket = _socket;
                if (socket is not null)
                {
                    var payload = BeaconCodec.Encode(DiscoveryPacket.Reply(provider(), packet.ProbeId));
                    SendRaw(payload, socket, new IPEndPoint(remote.Address, remote.Port));
                }
            }

            return;
        }

        if (packet.Receiver is { } receiver && !string.IsNullOrWhiteSpace(receiver.DeviceId)
            && packet.Kind == DiscoveryKinds.Announce)
        {
            receiver.Address = remote.Address.ToString();
            receiver.LastSeenUtc = DateTime.UtcNow;
            ReceiverAnnounced?.Invoke(this, receiver);
        }
        var server = packet.Server;
        if (server is null || string.IsNullOrWhiteSpace(server.ServerId)) return;

        if (string.Equals(packet.Kind, DiscoveryKinds.Goodbye, StringComparison.OrdinalIgnoreCase))
        {
            Remove(server.ServerId, "goodbye");
            return;
        }

        var announced = false;
        lock (_gate)
        {
            _servers[server.ServerId] = (server, DateTime.UtcNow);

            // A reply that echoes a probe we sent counts as a probe reply for diagnostics.
            if (!string.IsNullOrEmpty(packet.ProbeId) && _outstandingProbes.Remove(packet.ProbeId))
            {
                Interlocked.Increment(ref _probeReplies);
            }

            announced = true;
        }

        if (announced)
        {
            Interlocked.Increment(ref _announcementsReceived);
            ServerAnnounced?.Invoke(this, server);
        }
    }

    private void Remove(string serverId, string reason)
    {
        bool removed;
        lock (_gate) removed = _servers.Remove(serverId);

        if (!removed) return;

        _log.LogInformation("Beacon: server {ServerId} removed ({Reason})", serverId, reason);
        ServerGone?.Invoke(this, serverId);
    }

    private async Task PruneLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

                List<string> stale = [];
                var cutoff = DateTime.UtcNow - ServerTimeout;

                lock (_gate)
                {
                    foreach (var key in _servers.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToArray())
                    {
                        _servers.Remove(key);
                        stale.Add(key);
                    }
                }

                foreach (var serverId in stale)
                {
                    _log.LogInformation("Beacon: server {ServerId} timed out", serverId);
                    ServerGone?.Invoke(this, serverId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

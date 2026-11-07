using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lantern.Network.Mdns;

using Lantern.Core.Discovery;
using Lantern.Network.Dns;

/// <summary>
/// Finds Lantern servers on the LAN. It both listens to multicast announcements and
/// repeatedly asks the standard browse question, merging PTR/SRV/TXT/A records into
/// <see cref="DiscoveredLanternServer"/> instances. The Android client mirrors this
/// logic with NsdManager; the PC app uses it to verify its own advertisement and to
/// spot a second Lantern instance on the network.
/// </summary>
public sealed class MdnsBrowser : IAsyncDisposable
{
    private readonly string _serviceTypeFqdn;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredLanternServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPAddress> _joined = [];

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _queryLoop;

    public MdnsBrowser(string serviceTypeFqdn = DiscoveryConstants.ServiceTypeFqdn, ILogger<MdnsBrowser>? logger = null)
    {
        _serviceTypeFqdn = DnsName.Canonical(serviceTypeFqdn);
        _log = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Default 5353; matches the responder so announcements can be heard.</summary>
    public int Port { get; init; } = DiscoveryConstants.MdnsPort;

    /// <summary>How often the browse question is repeated.</summary>
    public TimeSpan QueryInterval { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>A server that stays silent for this long is reported as lost.</summary>
    public TimeSpan ServerTimeout { get; init; } = DiscoveryConstants.ServerTimeout;

    public bool IsRunning => _socket is not null;

    /// <summary>Runs a browse query straight away instead of waiting for the next tick.</summary>
    public bool QueryOnStart { get; init; } = true;

    public IReadOnlyList<IPAddress> JoinedInterfaces => _joined;

    /// <summary>Snapshot of the servers heard from within <see cref="ServerTimeout"/>.</summary>
    public IReadOnlyList<DiscoveredLanternServer> Servers
    {
        get
        {
            lock (_gate) return [.. _servers.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public event EventHandler<DiscoveredLanternServer>? ServerFound;

    public event EventHandler<DiscoveredLanternServer>? ServerUpdated;

    public event EventHandler<DiscoveredLanternServer>? ServerLost;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_socket is not null) return Task.CompletedTask;

        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            var group = IPAddress.Parse(DiscoveryConstants.MdnsMulticastGroup);
            foreach (var address in LanAddressResolver.GetLanAddresses())
            {
                try
                {
                    socket.JoinMulticastGroup(group, address);
                    _joined.Add(address);
                }
                catch (SocketException ex)
                {
                    _log.LogDebug(ex, "mDNS: browser could not join {Group} on {Address}", group, address);
                }
            }
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException($"Could not open the mDNS browser socket on UDP port {Port}. {ex.Message}", ex);
        }

        _socket = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _queryLoop = Task.Run(() => QueryLoopAsync(_cts.Token));
        _log.LogInformation("mDNS browser listening for {Service} on port {Port}", _serviceTypeFqdn, Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var socket = _socket;
        if (socket is null) return;

        _cts?.Cancel();
        _socket = null;

        try
        {
            foreach (var task in new[] { _receiveLoop, _queryLoop })
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
            _queryLoop = null;
            _joined.Clear();
            lock (_gate) _servers.Clear();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Sends the browse question immediately (multicast, unicast answer requested).</summary>
    public void Query()
    {
        var socket = _socket;
        if (socket is null) return;

        var message = DnsMessage.CreateQuery(_serviceTypeFqdn, DnsRecordType.Ptr, unicastResponse: true);
        var payload = message.ToBytes(compressNames: false);

        try
        {
            socket.Send(payload, payload.Length, new IPEndPoint(IPAddress.Parse(DiscoveryConstants.MdnsMulticastGroup), Port));
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "mDNS: browse query failed");
        }
        catch (ObjectDisposedException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveAsync(ct).ConfigureAwait(false);

                DnsMessage message;
                try
                {
                    message = DnsMessage.Parse(received.Buffer);
                }
                catch (DnsFormatException)
                {
                    continue;
                }

                if (message.IsResponse) Merge(message, received.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "mDNS: browser receive error, continuing");
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "mDNS: unexpected browser error");
            }
        }
    }

    private async Task QueryLoopAsync(CancellationToken ct)
    {
        try
        {
            if (QueryOnStart)
            {
                Query();
                SendEnumerationQuery();
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(QueryInterval, ct).ConfigureAwait(false);
                Query();
                Prune();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Asks which service types exist – diagnostics only ("is mDNS alive at all?").</summary>
    private void SendEnumerationQuery()
    {
        var socket = _socket;
        if (socket is null) return;

        var payload = DnsMessage.CreateQuery(
                DiscoveryConstants.ServiceEnumerationFqdn,
                DnsRecordType.Ptr,
                unicastResponse: true)
            .ToBytes(compressNames: false);

        try
        {
            socket.Send(payload, payload.Length, new IPEndPoint(IPAddress.Parse(DiscoveryConstants.MdnsMulticastGroup), Port));
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "mDNS: service enumeration query failed");
        }
        catch (ObjectDisposedException) { }
    }

    private void Prune()
    {
        List<DiscoveredLanternServer> lost = [];
        var cutoff = DateTime.UtcNow - ServerTimeout;

        lock (_gate)
        {
            foreach (var key in _servers.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToArray())
            {
                lost.Add(_servers[key]);
                _servers.Remove(key);
            }
        }

        foreach (var server in lost)
        {
            _log.LogInformation("mDNS: server gone {Server}", server);
            ServerLost?.Invoke(this, server);
        }
    }

    private void Merge(DnsMessage message, IPEndPoint remote)
    {
        List<DiscoveredLanternServer> created = [];
        List<DiscoveredLanternServer> changed = [];

        lock (_gate)
        {
            var records = message.Answers
                .Concat(message.Additionals)
                .Concat(message.Authorities)
                .ToArray();

            // Addresses first: an A record may be listed before or after the SRV that needs it.
            foreach (var address in records.OfType<ARecord>())
            {
                var hostKey = DnsName.Canonical(address.Name);
                if (address.TtlSeconds == 0) _hostAddresses.Remove(hostKey);
                else _hostAddresses[hostKey] = address.Address;
            }

            foreach (var record in records)
            {
                switch (record)
                {
                    case PtrRecord ptr when DnsName.Canonical(ptr.Name) == _serviceTypeFqdn:
                        if (ptr.TtlSeconds == 0) RemoveLocked(DnsName.Canonical(ptr.Target), "goodbye");
                        else if (IsInstanceOfService(ptr.Target)) Track(ptr.Target, remote, created);
                        break;

                    case SrvRecord srv when IsInstanceOfService(srv.Name):
                        if (srv.TtlSeconds == 0)
                        {
                            RemoveLocked(DnsName.Canonical(srv.Name), "goodbye");
                            break;
                        }

                        var srvEntry = Track(srv.Name, remote, created);
                        if (srvEntry.Host != srv.Target || srvEntry.Port != srv.Port)
                        {
                            srvEntry.Host = srv.Target;
                            srvEntry.Port = srv.Port;
                            changed.Add(srvEntry);
                        }
                        break;

                    case TxtRecord txt when IsInstanceOfService(txt.Name):
                        if (txt.TtlSeconds == 0) break;
                        var txtEntry = Track(txt.Name, remote, created);
                        ApplyTxt(txtEntry, txt);
                        changed.Add(txtEntry);
                        break;
                }
            }

            // Attach the addresses now that every record of the datagram has been read.
            foreach (var entry in _servers.Values)
            {
                if (entry.Host is null) continue;
                if (!_hostAddresses.TryGetValue(DnsName.Canonical(entry.Host), out var address)) continue;
                if (Equals(entry.Address, address)) continue;

                entry.Address = address;
                changed.Add(entry);
            }
        }

        foreach (var server in created)
        {
            _log.LogInformation("mDNS: server found {Server}", server);
            ServerFound?.Invoke(this, server);
        }

        foreach (var server in changed.Where(s => !created.Contains(s)).Distinct())
        {
            ServerUpdated?.Invoke(this, server);
        }
    }

    private DiscoveredLanternServer Track(string instanceFqdn, IPEndPoint remote, List<DiscoveredLanternServer> created)
    {
        var key = DnsName.Canonical(instanceFqdn);
        if (_servers.TryGetValue(key, out var existing))
        {
            existing.LastSeenUtc = DateTime.UtcNow;
            return existing;
        }

        var entry = new DiscoveredLanternServer { InstanceFqdn = instanceFqdn };
        if (remote.Address is not null) entry.Address = remote.Address;
        _servers[key] = entry;
        created.Add(entry);
        return entry;
    }

    private static void ApplyTxt(DiscoveredLanternServer entry, TxtRecord record)
    {
        entry.Merge(record.Values);

        var name = entry.Get(DiscoveryConstants.TxtName);
        if (!string.IsNullOrWhiteSpace(name)) entry.Name = name;

        var id = entry.Get(DiscoveryConstants.TxtServerId);
        if (!string.IsNullOrWhiteSpace(id)) entry.ServerId = id;
    }

    private void RemoveLocked(string key, string reason)
    {
        if (!_servers.Remove(key, out var removed) || removed is null) return;

        _log.LogInformation("mDNS: server removed ({Reason}) {Server}", reason, removed);
        ServerLost?.Invoke(this, removed);
    }

    private bool IsInstanceOfService(string name) =>
        DnsName.IsWithin(name, _serviceTypeFqdn) &&
        !string.Equals(DnsName.Canonical(name), _serviceTypeFqdn, StringComparison.Ordinal);

    /// <summary>
    /// One-shot resolve of a known instance: sends unicast SRV/TXT (then A) queries to the
    /// given address and waits on a private ephemeral port, so it also works when the access
    /// point filters multicast traffic.
    /// </summary>
    public async Task<DiscoveredLanternServer?> ResolveAsync(
        IPAddress address,
        string instanceFqdn,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var target = new IPEndPoint(address, Port);
        SendQuery(socket, target, instanceFqdn, DnsRecordType.Srv);
        SendQuery(socket, target, instanceFqdn, DnsRecordType.Txt);

        var deadline = DateTime.UtcNow + timeout;
        var key = DnsName.Canonical(instanceFqdn);
        var hostQueried = false;
        DiscoveredLanternServer? entry = null;

        while (DateTime.UtcNow < deadline)
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
                    _log.LogDebug(ex, "mDNS: resolve of {Instance} failed", instanceFqdn);
                    break;
                }
                catch (ObjectDisposedException) { break; }
            }

            DnsMessage message;
            try
            {
                message = DnsMessage.Parse(received.Buffer);
            }
            catch (DnsFormatException)
            {
                continue;
            }

            if (!message.IsResponse) continue;
            Merge(message, received.RemoteEndPoint);

            bool haveAddress, haveTxt;
            lock (_gate)
            {
                _servers.TryGetValue(key, out entry);
                haveAddress = entry?.Host is not null && _hostAddresses.ContainsKey(DnsName.Canonical(entry.Host));
                haveTxt = entry is { Txt.Count: > 0 };
            }

            if (entry is not { IsResolved: true }) continue;

            if (!hostQueried && entry.Host is not null)
            {
                hostQueried = true;
                SendQuery(socket, target, entry.Host, DnsRecordType.A);
            }

            // TXT arrives in its own answer (the SRV response only carries the address), so wait
            // for both before reporting the server: the id/name/version all live in TXT.
            if (haveAddress && haveTxt) break;
        }

        return entry;
    }

    private static void SendQuery(UdpClient socket, IPEndPoint target, string name, DnsRecordType type)
    {
        var payload = DnsMessage.CreateQuery(name, type, unicastResponse: true).ToBytes(compressNames: false);
        try
        {
            socket.Send(payload, payload.Length, target);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}

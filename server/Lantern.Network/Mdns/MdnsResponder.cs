using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lantern.Network.Mdns;

using Lantern.Core.Discovery;
using Lantern.Network.Dns;

/// <summary>
/// Answers mDNS queries for one Lantern service instance: PTR (service type and
/// service enumeration), SRV, TXT and A. Responses go unicast to whoever asked, which
/// is what Android's NsdManager and legacy resolvers expect, and the records are
/// additionally announced by multicast on start and then every
/// <see cref="AnnounceInterval"/>.
/// </summary>
public sealed class MdnsResponder : IAsyncDisposable
{
    private readonly MdnsServiceDefinition _service;
    private readonly ILogger _log;
    private readonly List<IPAddress> _joined = [];

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _announceLoop;
    private long _queriesAnswered;
    private long _responsesSent;
    private long _conflicts;

    public MdnsResponder(MdnsServiceDefinition service, ILogger<MdnsResponder>? logger = null)
    {
        _service = service;
        _log = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Default 5353; overridable so tests can run without touching the system port.</summary>
    public int Port { get; init; } = DiscoveryConstants.MdnsPort;

    /// <summary>How often the records are re-announced (keeps resolvers warm).</summary>
    public TimeSpan AnnounceInterval { get; init; } = DiscoveryConstants.MdnsAnnounceInterval;

    public bool IsRunning => _socket is not null;

    public long QueriesAnswered => Interlocked.Read(ref _queriesAnswered);

    public long ResponsesSent => Interlocked.Read(ref _responsesSent);

    /// <summary>Number of responses seen from another device claiming our names.</summary>
    public long ConflictsDetected => Interlocked.Read(ref _conflicts);

    public IReadOnlyList<IPAddress> JoinedInterfaces => _joined;

    /// <summary>Raised when another device answers with one of our names.</summary>
    public event EventHandler<string>? ConflictDetected;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_socket is not null) return Task.CompletedTask;

        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.EnableBroadcast = true;
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

            var group = IPAddress.Parse(DiscoveryConstants.MdnsMulticastGroup);
            foreach (var address in _service.Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                try
                {
                    socket.JoinMulticastGroup(group, address);
                    _joined.Add(address);
                }
                catch (SocketException ex)
                {
                    _log.LogDebug(ex, "mDNS: could not join {Group} on {Address}", group, address);
                }
            }

            if (_joined.Count == 0)
            {
                socket.JoinMulticastGroup(group);
                _log.LogDebug("mDNS: joined {Group} on the default interface only", group);
            }
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"Could not open the mDNS socket on UDP port {Port}. Another mDNS service may be using it. {ex.Message}", ex);
        }

        _socket = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _announceLoop = Task.Run(() => AnnounceLoopAsync(_cts.Token));
        _log.LogInformation("mDNS responder started: {Service}", _service.Describe());
        return Task.CompletedTask;
    }

    /// <summary>Stops the responder and (by default) sends the goodbye packets.</summary>
    public async Task StopAsync(bool sendGoodbye = true)
    {
        var socket = _socket;
        if (socket is null) return;

        if (sendGoodbye) Announce(goodbye: true);

        _cts?.Cancel();
        _socket = null;

        try
        {
            foreach (var task in new[] { _receiveLoop, _announceLoop })
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
            _announceLoop = null;
            _joined.Clear();
            _log.LogInformation("mDNS responder stopped after {Answers} answers", QueriesAnswered);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                HandleDatagram(received.Buffer, received.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // Wi-Fi roaming / VPN changes surface as socket errors; keep serving.
                _log.LogDebug(ex, "mDNS: receive error, continuing");
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "mDNS: unexpected error while handling a datagram");
            }
        }
    }

    private void HandleDatagram(byte[] datagram, IPEndPoint remote)
    {
        DnsMessage message;
        try
        {
            message = DnsMessage.Parse(datagram);
        }
        catch (DnsFormatException ex)
        {
            _log.LogTrace(ex, "mDNS: ignoring malformed datagram from {Remote}", remote);
            return;
        }

        if (message.IsResponse)
        {
            DetectConflict(message, remote);
            return;
        }

        if (message.Questions.Count == 0) return;
        Interlocked.Increment(ref _queriesAnswered);

        var response = new DnsMessage
        {
            Id = message.Id,
            Flags = DnsFlags.Response | DnsFlags.Authoritative,
        };

        foreach (var question in message.Questions)
        {
            if (IsSuppressedByKnownAnswer(message, question)) continue;
            AnswerQuestion(response, question);
        }

        if (response.Answers.Count == 0) return;

        AddMissingAdditionals(response);
        Interlocked.Increment(ref _responsesSent);
        Send(response.ToBytes(), remote);
        _log.LogTrace("mDNS: answered {Questions} from {Remote}", message.Questions.Count, remote);
    }

    /// <summary>RFC 6762 section 7.1: do not repeat a record the asker already knows.</summary>
    private bool IsSuppressedByKnownAnswer(DnsMessage query, DnsQuestion question)
    {
        if (query.Answers.Count == 0) return false;
        var threshold = question.Type == DnsRecordType.Ptr
            ? DiscoveryConstants.MdnsSharedTtlSeconds / 2
            : DiscoveryConstants.MdnsHostTtlSeconds / 2;

        foreach (var answer in query.Answers)
        {
            if (answer.Type != question.Type) continue;
            if (!string.Equals(DnsName.Canonical(answer.Name), DnsName.Canonical(question.Name), StringComparison.Ordinal)) continue;
            if (answer.TtlSeconds >= threshold) return true;
        }

        return false;
    }

    private void DetectConflict(DnsMessage response, IPEndPoint remote)
    {
        if (remote.Address is null) return;
        if (_service.Addresses.Contains(remote.Address)) return;

        foreach (var record in response.Answers)
        {
            if (record.TtlSeconds == 0) continue;
            if (!record.Name.Equals(_service.InstanceFqdn, StringComparison.OrdinalIgnoreCase) &&
                !record.Name.Equals(_service.HostName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Interlocked.Increment(ref _conflicts);
            var message = $"Another device at {remote} also claims '{record.Name}'. " +
                          "Give this PC a unique Lantern name to avoid confusing clients.";
            _log.LogWarning("mDNS: {Message}", message);
            ConflictDetected?.Invoke(this, message);
            return;
        }
    }

    private void AnswerQuestion(DnsMessage response, DnsQuestion question)
    {
        var name = DnsName.Canonical(question.Name);
        var type = question.Type;

        // "Which service types live here?" – answered with the type, not the instance.
        if (name == DnsName.Canonical(DiscoveryConstants.ServiceEnumerationFqdn) &&
            type is DnsRecordType.Ptr or DnsRecordType.Any)
        {
            response.Answers.Add(new PtrRecord(DiscoveryConstants.ServiceEnumerationFqdn, _service.ServiceTypeFqdn)
            {
                TtlSeconds = DiscoveryConstants.MdnsSharedTtlSeconds,
            });
            return;
        }

        // "Who offers _lantern._tcp?" – the browse query every client starts with.
        if (name == _service.ServiceTypeFqdn && type is DnsRecordType.Ptr or DnsRecordType.Any)
        {
            response.Answers.Add(new PtrRecord(_service.ServiceTypeFqdn, _service.InstanceFqdn)
            {
                TtlSeconds = DiscoveryConstants.MdnsSharedTtlSeconds,
            });
            return;
        }

        if (name == DnsName.Canonical(_service.InstanceFqdn))
        {
            if (type is DnsRecordType.Srv or DnsRecordType.Any) response.Answers.Add(BuildSrv());
            if (type is DnsRecordType.Txt or DnsRecordType.Any) response.Answers.Add(BuildTxt());
            return;
        }

        if (name == _service.HostName && type is DnsRecordType.A or DnsRecordType.Any)
        {
            foreach (var record in BuildAddressRecords()) response.Answers.Add(record);
        }
    }

    private SrvRecord BuildSrv(uint ttl = DiscoveryConstants.MdnsHostTtlSeconds) =>
        new(_service.InstanceFqdn, 0, 0, _service.Port, _service.HostName)
        {
            TtlSeconds = ttl,
            CacheFlush = true,
        };

    private TxtRecord BuildTxt(uint ttl = DiscoveryConstants.MdnsHostTtlSeconds) =>
        new(_service.InstanceFqdn, _service.Txt)
        {
            TtlSeconds = ttl,
            CacheFlush = true,
        };

    private IEnumerable<DnsRecord> BuildAddressRecords(uint ttl = DiscoveryConstants.MdnsHostTtlSeconds)
    {
        foreach (var address in _service.Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
        {
            yield return new ARecord(_service.HostName, address) { TtlSeconds = ttl, CacheFlush = true };
        }
    }

    /// <summary>DNS-SD wants SRV/TXT/A attached to a PTR answer so one round trip resolves everything.</summary>
    private void AddMissingAdditionals(DnsMessage response)
    {
        bool Known(DnsRecordType type) =>
            response.Answers.Any(r => r.Type == type) || response.Additionals.Any(r => r.Type == type);

        if (response.Answers.Any(r => r.Type == DnsRecordType.Ptr))
        {
            if (!Known(DnsRecordType.Srv)) response.Additionals.Add(BuildSrv());
            if (!Known(DnsRecordType.Txt)) response.Additionals.Add(BuildTxt());
            if (!Known(DnsRecordType.A)) response.Additionals.AddRange(BuildAddressRecords());
            return;
        }

        if (response.Answers.Any(r => r.Type == DnsRecordType.Srv) && !Known(DnsRecordType.A))
        {
            response.Additionals.AddRange(BuildAddressRecords());
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            // RFC 6762 section 8.3: repeat the initial announcement a few times.
            for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
            {
                Announce(goodbye: false);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(AnnounceInterval, ct).ConfigureAwait(false);
                Announce(goodbye: false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Unsolicited multicast response that teaches the LAN about this server.</summary>
    private void Announce(bool goodbye)
    {
        var sharedTtl = goodbye ? 0u : DiscoveryConstants.MdnsSharedTtlSeconds;
        var hostTtl = goodbye ? 0u : DiscoveryConstants.MdnsHostTtlSeconds;

        var message = new DnsMessage
        {
            Id = 0,
            Flags = DnsFlags.Response | DnsFlags.Authoritative,
        };

        message.Answers.Add(new PtrRecord(_service.ServiceTypeFqdn, _service.InstanceFqdn) { TtlSeconds = sharedTtl });
        message.Answers.Add(new PtrRecord(DiscoveryConstants.ServiceEnumerationFqdn, _service.ServiceTypeFqdn) { TtlSeconds = sharedTtl });
        message.Additionals.Add(BuildSrv(hostTtl));
        message.Additionals.Add(BuildTxt(hostTtl));
        message.Additionals.AddRange(BuildAddressRecords(hostTtl));

        Send(message.ToBytes(), MulticastEndpoint);
        if (!goodbye) Interlocked.Increment(ref _responsesSent);
    }

    private IPEndPoint MulticastEndpoint => new(IPAddress.Parse(DiscoveryConstants.MdnsMulticastGroup), Port);

    private void Send(byte[] payload, IPEndPoint endpoint)
    {
        var socket = _socket;
        if (socket is null) return;

        try
        {
            socket.Send(payload, payload.Length, endpoint);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "mDNS: send to {Endpoint} failed", endpoint);
        }
        catch (ObjectDisposedException) { }
    }
}

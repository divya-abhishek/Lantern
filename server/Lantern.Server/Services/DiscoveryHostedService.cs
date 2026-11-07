using System.Net;
using System.Net.Sockets;
using Lantern.Core.Discovery;
using Lantern.Network;
using Lantern.Network.Discovery;
using Lantern.Network.Mdns;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// Advertises this server on the LAN: mDNS (primary - what Android's NsdManager uses) and
/// the UDP beacon fallback, plus the probe responder that answers "who is there?".
/// </summary>
public sealed class DiscoveryHostedService : IHostedService, IAsyncDisposable
{
    private readonly ServerRuntime _runtime;
    private readonly SettingsStore _settings;
    private readonly ConnectionHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DiscoveryHostedService> _logger;

    private MdnsResponder? _mdns;
    private UdpBeaconBroadcaster? _beacon;
    private UdpBeaconListener? _probeResponder;

    public DiscoveryHostedService(
        ServerRuntime runtime,
        SettingsStore settings,
        ConnectionHub hub,
        ILoggerFactory loggerFactory,
        ILogger<DiscoveryHostedService> logger)
    {
        _runtime = runtime;
        _settings = settings;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public bool MdnsRunning => _mdns?.IsRunning == true;

    public bool BeaconRunning => _beacon?.IsRunning == true;

    public long ConflictsDetected => _mdns?.ConflictsDetected ?? 0;
    public ReceiverDirectory Receivers { get; } = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (settings.EnableMdns)
        {
            try
            {
                var definition = BuildDefinition();
                var responder = new MdnsResponder(definition, _loggerFactory.CreateLogger<MdnsResponder>());
                responder.ConflictDetected += OnMdnsConflict;
                await responder.StartAsync(cancellationToken).ConfigureAwait(false);
                _mdns = responder;
                _logger.LogInformation(
                    "mDNS advertising {Instance} -> {Host}:{Port}",
                    definition.InstanceFqdn, definition.HostName, definition.Port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mDNS discovery could not start (is port {Port} in use?)", DiscoveryConstants.MdnsPort);
                _mdns = null;
            }
        }

        if (!settings.EnableUdpBeacon) return;

        try
        {
            var beacon = new UdpBeaconBroadcaster(
                () => _runtime.BuildDiscoveryDto(),
                _loggerFactory.CreateLogger<UdpBeaconBroadcaster>());
            await beacon.StartAsync(cancellationToken).ConfigureAwait(false);
            _beacon = beacon;

            var probes = new UdpBeaconListener(_loggerFactory.CreateLogger<UdpBeaconListener>())
            {
                AnswerProbes = true,
                ServerProvider = () => _runtime.BuildDiscoveryDto(),
                ProbeOnStart = false,
            };
            probes.ReceiverAnnounced += (_, receiver) => Receivers.Update(receiver);
            await probes.StartAsync(cancellationToken).ConfigureAwait(false);
            _probeResponder = probes;

            _logger.LogInformation(
                "UDP beacon announcing on {Port} and answering probes", DiscoveryConstants.BeaconPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP beacon discovery could not start");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_probeResponder is not null)
        {
            await _probeResponder.DisposeAsync().ConfigureAwait(false);
            _probeResponder = null;
        }

        if (_beacon is not null)
        {
            // Sends the goodbye packet so clients drop this server immediately.
            await _beacon.StopAsync().ConfigureAwait(false);
            _beacon = null;
        }

        if (_mdns is not null)
        {
            await _mdns.StopAsync().ConfigureAwait(false);
            _mdns = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>DNS-SD instance/TXT records describing this server.</summary>
    public MdnsServiceDefinition BuildDefinition()
    {
        var settings = _settings.Current;
        var definition = new MdnsServiceDefinition(
            settings.ServerName,
            _runtime.Identity.HostName,
            (ushort)Math.Clamp(settings.Port, 1, 65535));

        definition.Txt[DiscoveryConstants.TxtServerId] = _runtime.Identity.ServerId;
        definition.Txt[DiscoveryConstants.TxtName] = settings.ServerName;
        definition.Txt[DiscoveryConstants.TxtVersion] = _runtime.Version;
        definition.Txt[DiscoveryConstants.TxtApiVersion] = _runtime.ApiVersion;
        definition.Txt[DiscoveryConstants.TxtMachineName] = Environment.MachineName;
        definition.Txt[DiscoveryConstants.TxtRequiresPairing] = settings.RequirePairing ? "1" : "0";
        definition.Txt[DiscoveryConstants.TxtProtocols] =
            "http-range,rtsp";

        foreach (var address in LanAddressResolver.GetLanAddresses()
                     .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
        {
            definition.Addresses.Add(address);
        }

        return definition;
    }

    private void OnMdnsConflict(object? sender, string detail)
    {
        _logger.LogWarning("mDNS name conflict: {Detail}", detail);
        _ = _hub.BroadcastErrorAsync(
            "mdns_conflict",
            "Another device on this network is using this server's mDNS name.",
            detail);
    }
}

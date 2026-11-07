namespace Lantern.Core.Dtos;

using Lantern.Core.Api;
using Lantern.Core.Discovery;

/// <summary>
/// A single UDP datagram exchanged on <see cref="DiscoveryConstants.BeaconPort"/>.
/// The same shape carries announcements, probes and replies so both platforms only
/// need one serializer.
/// </summary>
public sealed class DiscoveryPacket
{
    /// <summary>Always <see cref="DiscoveryConstants.Protocol"/>; used to drop foreign traffic.</summary>
    public string Protocol { get; set; } = DiscoveryConstants.Protocol;

    public int Version { get; set; } = DiscoveryConstants.PacketVersion;

    /// <summary>One of <see cref="DiscoveryKinds"/>.</summary>
    public string Kind { get; set; } = DiscoveryKinds.Announce;

    /// <summary>Echoed back by replies so a probing client can match them.</summary>
    public string? ProbeId { get; set; }

    /// <summary>Set by a probing client.</summary>
    public string? ClientId { get; set; }

    public string? ClientName { get; set; }

    /// <summary>Set for announcements, replies and goodbyes (null for probes).</summary>
    public DiscoveryServerDto? Server { get; set; }
    public DiscoveryReceiverDto? Receiver { get; set; }

    public DateTime SentUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True when the packet belongs to this protocol and layout.</summary>
    public bool IsCompatible =>
        string.Equals(Protocol, DiscoveryConstants.Protocol, StringComparison.OrdinalIgnoreCase);

    public bool IsProbe => string.Equals(Kind, DiscoveryKinds.Probe, StringComparison.OrdinalIgnoreCase);

    public bool IsAnnouncement =>
        string.Equals(Kind, DiscoveryKinds.Announce, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Kind, DiscoveryKinds.Goodbye, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the reply a server sends back to a probe.</summary>
    public static DiscoveryPacket Reply(DiscoveryServerDto server, string? probeId) => new()
    {
        Kind = DiscoveryKinds.Announce,
        ProbeId = probeId,
        Server = server,
    };
}

/// <summary>Everything a client needs to open an HTTP session with a discovered server.</summary>
public sealed class DiscoveryServerDto
{
    public required string ServerId { get; set; }
    public required string Name { get; set; }

    /// <summary>HTTP port (LanternSettings.Port).</summary>
    public int Port { get; set; }

    public string ApiVersion { get; set; } = "v1";
    public string Version { get; set; } = "1.0.0";
    public string? MachineName { get; set; }

    /// <summary>Stable mDNS host name, e.g. "lantern-4f2a1c.local" (preferred over raw IPs).</summary>
    public string? Host { get; set; }

    /// <summary>LAN addresses of the machine, best first.</summary>
    public List<string> Addresses { get; set; } = [];

    public bool RequiresPairing { get; set; } = true;

    public List<string> StreamingProtocols { get; set; } = ["http-range", "rtsp"];

    public long UptimeSeconds { get; set; }

    /// <summary>True when transcoding is possible (ffmpeg detected) – lets the client grey out quality settings.</summary>
    public bool TranscodingAvailable { get; set; } = true;

    /// <summary>Picks the address a client should dial: the DNS name when present, else the first address.</summary>
    public string? BestHost() =>
        !string.IsNullOrWhiteSpace(Host) ? Host :
        Addresses.Count > 0 ? Addresses[0] : null;

    /// <summary>http://host:port or null when nothing is known yet.</summary>
    public Uri? BuildBaseUri(string? overrideHost = null)
    {
        var host = overrideHost;
        if (string.IsNullOrWhiteSpace(host)) host = BestHost();
        if (string.IsNullOrWhiteSpace(host) || Port <= 0) return null;
        return new Uri($"http://{host}:{Port}/");
    }

    /// <summary>Absolute URL of GET /api/v1/server/info used to verify the server before pairing.</summary>
    public Uri? BuildServerInfoUri(string? overrideHost = null)
    {
        var baseUri = BuildBaseUri(overrideHost);
        return baseUri is null ? null : new Uri(baseUri, ApiRoutes.ServerInfo.TrimStart('/'));
    }

    /// <summary>One line summary for logs / the diagnostics screen.</summary>
    public override string ToString() =>
        $"{Name} ({ServerId}) http://{BestHost() ?? "?"}:{Port} v{Version} api={ApiVersion} pairing={RequiresPairing}";
}

public sealed class DiscoveryReceiverDto
{
    public bool SupportsConnect { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "Android receiver";
    public string DeviceType { get; set; } = "Android";
    public string Address { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
    public bool Paired { get; set; }
    public bool Online => DateTime.UtcNow - LastSeenUtc < TimeSpan.FromSeconds(15);
    public string Summary => $"{DeviceType} · {Address} · {(Paired ? "Paired" : "Unpaired")} · {(Online ? "Online" : "Offline")}";
}

using System.Net;

namespace Lantern.Network.Mdns;

using Lantern.Core.Discovery;
using Lantern.Core.Dtos;

/// <summary>
/// A Lantern server discovered over mDNS, merged from its PTR/SRV/TXT/A records.
/// Converted to <see cref="DiscoveryServerDto"/> so mDNS and UDP beacon results can be
/// handled by one code path on the client.
/// </summary>
public sealed class DiscoveredLanternServer
{
    public required string InstanceFqdn { get; init; }

    public string ServerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Target host of the SRV record, e.g. "lantern-4f2a1c.local".</summary>
    public string? Host { get; set; }

    public ushort Port { get; set; }

    public IPAddress? Address { get; set; }

    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime FirstSeenUtc { get; init; } = DateTime.UtcNow;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True once the SRV record arrived (without it the endpoint is unknown).</summary>
    public bool IsResolved => Port > 0 && !string.IsNullOrWhiteSpace(Host);

    public string? Version => Get(DiscoveryConstants.TxtVersion);

    public string ApiVersion => Get(DiscoveryConstants.TxtApiVersion) ?? "v1";

    public string? MachineName => Get(DiscoveryConstants.TxtMachineName);

    public bool RequiresPairing =>
        string.Equals(Get(DiscoveryConstants.TxtRequiresPairing), "1", StringComparison.Ordinal) ||
        string.Equals(Get(DiscoveryConstants.TxtRequiresPairing), "true", StringComparison.OrdinalIgnoreCase);

    public string? Get(string key) => Txt.TryGetValue(key, out var value) ? value : null;

    public void Merge(Dictionary<string, string> values)
    {
        foreach (var (key, value) in values) Txt[key] = value;
    }

    /// <summary>Normalizes the mDNS view into the shared discovery DTO.</summary>
    public DiscoveryServerDto ToDiscoveryServer()
    {
        var protocols = Get(DiscoveryConstants.TxtProtocols);
        return new DiscoveryServerDto
        {
            ServerId = string.IsNullOrWhiteSpace(ServerId) ? InstanceFqdn : ServerId,
            Name = string.IsNullOrWhiteSpace(Name) ? InstanceFqdn.Split('.')[0] : Name,
            Port = Port,
            Host = Host,
            Addresses = Address is null ? [] : [Address.ToString()],
            Version = Version ?? "1.0.0",
            ApiVersion = ApiVersion,
            MachineName = MachineName,
            RequiresPairing = RequiresPairing,
            StreamingProtocols = string.IsNullOrWhiteSpace(protocols)
                ? ["http-range", "hls"]
                : [.. protocols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            UptimeSeconds = (long)(DateTime.UtcNow - FirstSeenUtc).TotalSeconds,
        };
    }

    public override string ToString() =>
        $"{Name} [{ServerId}] {(Host ?? "?")}:{Port} addr={Address?.ToString() ?? "?"} v{Version ?? "?"}";
}

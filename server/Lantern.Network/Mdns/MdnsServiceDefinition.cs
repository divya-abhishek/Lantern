using System.Net;

namespace Lantern.Network.Mdns;

using Lantern.Core.Discovery;
using Lantern.Network.Dns;

/// <summary>
/// Everything the responder advertises over mDNS: one DNS-SD service instance
/// (<c>InstanceName._lantern._tcp.local</c>), its SRV target host, TXT attributes
/// and the IPv4 addresses of the machine.
/// </summary>
public sealed class MdnsServiceDefinition
{
    public MdnsServiceDefinition(string instanceName, string hostName, ushort port, string serviceTypeFqdn = DiscoveryConstants.ServiceTypeFqdn)
    {
        InstanceName = DnsName.SanitizeLabel(instanceName);
        HostName = DnsName.Canonical(hostName);
        Port = port;
        ServiceTypeFqdn = DnsName.Canonical(serviceTypeFqdn);
    }

    /// <summary>Human readable instance label, e.g. "Lantern PC".</summary>
    public string InstanceName { get; }

    /// <summary>Service type FQDN, e.g. "_lantern._tcp.local".</summary>
    public string ServiceTypeFqdn { get; }

    /// <summary>Stable host name of this server, e.g. "lantern-4f2a1c.local".</summary>
    public string HostName { get; }

    public ushort Port { get; }

    /// <summary>Full instance FQDN, e.g. "Lantern PC._lantern._tcp.local".</summary>
    public string InstanceFqdn => DnsName.ServiceInstance(InstanceName, ServiceTypeFqdn);

    /// <summary>DNS-SD attributes (see DiscoveryConstants.Txt*).</summary>
    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Advertised IPv4 addresses of this machine.</summary>
    public List<IPAddress> Addresses { get; } = [];

    /// <summary>True when the name belongs to this responder (used for conflict detection).</summary>
    public bool Owns(string name)
    {
        var candidate = DnsName.Canonical(name);
        return candidate == DnsName.Canonical(InstanceFqdn) ||
               candidate == ServiceTypeFqdn ||
               candidate == HostName;
    }

    public string Describe() =>
        $"{InstanceFqdn} -> {HostName}:{Port} [{string.Join(", ", Addresses)}] " +
        $"({string.Join(", ", Txt.Select(kv => $"{kv.Key}={kv.Value}"))})";
}

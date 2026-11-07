namespace Lantern.Core.Discovery;

using Lantern.Core.Api;

/// <summary>
/// Wire constants shared by the PC server and the Android client. Both ends must agree
/// on these values, so they live in Core with the rest of the API contract instead of
/// inside the networking implementation.
/// </summary>
public static class DiscoveryConstants
{
    /// <summary>Magic string that must be present in every discovery packet.</summary>
    public const string Protocol = "lantern";

    /// <summary>Layout version of the discovery packet. Bumped when the shape changes.</summary>
    public const int PacketVersion = 1;

    /// <summary>UDP port used for beacons and probes (HTTP lives on LanternSettings.Port).</summary>
    public const int BeaconPort = 8788;

    /// <summary>Multicast group the beacons are additionally sent to (works when broadcast is filtered).</summary>
    public const string BeaconMulticastGroup = "239.255.43.44";

    /// <summary>Standard mDNS multicast group.</summary>
    public const string MdnsMulticastGroup = "224.0.0.251";

    public const int MdnsPort = 5353;

    /// <summary>DNS-SD service type advertised by the server.</summary>
    public const string ServiceType = "_lantern._tcp";

    public const string ServiceDomain = "local";

    /// <summary>Fully qualified service type, e.g. "_lantern._tcp.local".</summary>
    public const string ServiceTypeFqdn = ServiceType + "." + ServiceDomain;

    /// <summary>Meta query that enumerates every service type present on the LAN.</summary>
    public const string ServiceEnumerationFqdn = "_services._dns-sd._udp.local";

    /// <summary>TTL of the shared PTR record (RFC 6762 recommends 75 minutes).</summary>
    public const uint MdnsSharedTtlSeconds = 4500;

    /// <summary>TTL of the cache-flush records (SRV/TXT/A) served by this responder.</summary>
    public const uint MdnsHostTtlSeconds = 120;

    // --- DNS-SD TXT record keys -------------------------------------------
    public const string TxtServerId = "id";
    public const string TxtName = "name";
    public const string TxtVersion = "ver";
    public const string TxtApiVersion = "api";
    public const string TxtMachineName = "mach";
    public const string TxtRequiresPairing = "pair";
    public const string TxtProtocols = "proto";

    /// <summary>Header the client must send with the pairing token (mirrors ApiRoutes).</summary>
    public const string TokenHeader = ApiRoutes.TokenHeader;

    /// <summary>An announcement is repeated this often so late clients still see the server.</summary>
    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(3);

    /// <summary>A server that has not been heard from within this window is treated as gone.</summary>
    public static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(12);

    /// <summary>mDNS re-announce interval; comfortably inside the SRV/A TTL of two minutes.</summary>
    public static readonly TimeSpan MdnsAnnounceInterval = TimeSpan.FromSeconds(45);
}

/// <summary>Values of <see cref="Lantern.Core.Dtos.DiscoveryPacket.Kind"/>.</summary>
public static class DiscoveryKinds
{
    /// <summary>Server -> LAN: "I am here" (also unicast as a reply to a probe).</summary>
    public const string Announce = "announce";

    /// <summary>Client -> LAN: "who is there?"</summary>
    public const string Probe = "probe";

    /// <summary>Server -> LAN: "I am shutting down".</summary>
    public const string Goodbye = "goodbye";
}

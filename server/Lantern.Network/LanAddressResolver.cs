using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lantern.Network;

/// <summary>
/// Finds the IPv4 addresses this PC can be reached on from the LAN. Used by the mDNS
/// responder (A records and multicast joins), by the UDP beacon (addresses in the
/// announcement) and by the desktop UI ("dial this address from the projector").
/// </summary>
public static class LanAddressResolver
{
    /// <summary>
    /// Adapter descriptions that usually mean "not the cable/Wi-Fi the projector is on".
    /// They are ranked last instead of being dropped: a Wi-Fi Direct bridge is a virtual
    /// adapter too, but it can still be the only working path to the projector.
    /// </summary>
    private static readonly string[] VirtualAdapterHints =
    [
        "loopback", "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "wsl",
        "docker", "tap-", "tun", "vpn", "pseudo", "bluetooth", "npcap", "bridge",
    ];

    /// <summary>
    /// Addresses of every usable IPv4 interface, best candidate first. APIPA (169.254/16)
    /// and loopback are excluded because they cannot reach a projector.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        List<(int Score, IPAddress Address)> candidates = [];

        foreach (var adapter in SafeGetAdapters())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (adapter.IsReceiveOnly) continue;

            var score = ScoreAdapter(adapter);
            if (score < 0) continue;

            foreach (var unicast in SafeGetUnicastAddresses(adapter))
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(address)) continue;
                if (IsApipa(address)) continue;

                candidates.Add((score, address));
            }
        }

        return [.. candidates
            .OrderBy(c => c.Score)
            .Select(c => c.Address)
            .Distinct()];
    }

    /// <summary>The address most likely to be reachable from the projector, or null when offline.</summary>
    public static IPAddress? GetPrimaryLanAddress() => GetLanAddresses().FirstOrDefault();

    /// <summary>All addresses plus loopback, used by tests and diagnostics displays.</summary>
    public static IReadOnlyList<IPAddress> GetLanAddressesWithLoopback() =>
        [.. GetLanAddresses(), IPAddress.Loopback];

    private static int ScoreAdapter(NetworkInterface adapter)
    {
        var description = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        var isVirtual = VirtualAdapterHints.Any(hint => description.Contains(hint, StringComparison.Ordinal));

        // Ethernet is the most reliable transport for 4K direct play, Wi-Fi next;
        // anything else (cellular, Wi-Fi Direct, unknown) is only used as a fallback.
        var score = adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => 0,
            NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetFx => 0,
            NetworkInterfaceType.Wireless80211 => 10,
            _ => 40,
        };

        if (isVirtual) score += 100;
        if (!HasGateway(adapter)) score += 20;
        return score;
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Count > 0;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static NetworkInterface[] SafeGetAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static IEnumerable<UnicastIPAddressInformation> SafeGetUnicastAddresses(NetworkInterface adapter)
    {
        IPInterfaceProperties properties;
        try
        {
            properties = adapter.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        return properties.UnicastAddresses;
    }
}

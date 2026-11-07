using System.Text;
using System.Text.Json;

namespace Lantern.Network.Discovery;

using Lantern.Core.Api;
using Lantern.Core.Dtos;

/// <summary>
/// JSON-in-UDP encoding of <see cref="DiscoveryPacket"/>. The payload is plain UTF-8 JSON so
/// the Android client can use its own serializer, and a cheap first-byte check discards
/// foreign traffic before paying for a JSON parse.
/// </summary>
public static class BeaconCodec
{
    /// <summary>Discovery packets are tiny; anything larger is not ours.</summary>
    public const int MaxPacketBytes = 4096;

    public static byte[] Encode(DiscoveryPacket packet) => Encoding.UTF8.GetBytes(ApiJson.Serialize(packet));

    public static bool TryDecode(ReadOnlySpan<byte> datagram, out DiscoveryPacket? packet)
    {
        packet = null;
        if (datagram.Length is 0 or > MaxPacketBytes) return false;
        if (datagram[0] != (byte)'{') return false;

        try
        {
            var decoded = JsonSerializer.Deserialize<DiscoveryPacket>(datagram, ApiJson.Options);
            if (decoded is null || !decoded.IsCompatible) return false;

            packet = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}

using Lantern.Core.Dtos;

namespace Lantern.Network.Discovery;

/// <summary>One server-owned view of available receivers. Pairing trust is separate.</summary>
public sealed class ReceiverDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveryReceiverDto> _receivers = new(StringComparer.OrdinalIgnoreCase);
    public void Update(DiscoveryReceiverDto receiver)
    {
        if (string.IsNullOrWhiteSpace(receiver.DeviceId)) return;
        lock (_gate) _receivers[receiver.DeviceId] = receiver;
    }
    public IReadOnlyList<DiscoveryReceiverDto> Snapshot()
    {
        lock (_gate)
        {
            foreach (var id in _receivers.Where(p => !p.Value.Online).Select(p => p.Key).ToArray()) _receivers.Remove(id);
            return _receivers.Values.OrderBy(r => r.Name).ToArray();
        }
    }
}

using System.Net;
using System.Security.Cryptography;

namespace Lantern.Server.Security;

/// <summary>One-shot PC-authorized connection requests; receiver consent redeems them.</summary>
public sealed class ReceiverInvitations(TimeProvider? timeProvider = null)
{
    private DateTime Now => (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    private sealed record Invitation(string DeviceId, string Address, DateTime Expires);
    private readonly Dictionary<string, Invitation> _pending = new();
    private readonly object _gate = new();
    private static string Address(string value) => IPAddress.Parse(value).MapToIPv6().ToString();

    public string Create(string deviceId, string address)
    {
        lock (_gate)
        {
            foreach (var key in _pending.Where(x => x.Value.Expires <= Now || x.Value.DeviceId == deviceId).Select(x => x.Key).ToArray())
                _pending.Remove(key);
            if (_pending.Count >= 64) throw new InvalidOperationException("Too many connection requests. Wait one minute.");
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _pending[TokenService.Hash(secret)] = new(deviceId, Address(address), Now.AddSeconds(60));
            return secret;
        }
    }

    public bool Consume(string secret, string deviceId, string? address)
    {
        if (secret.Length != 64 || !IPAddress.TryParse(address, out _)) return false;
        lock (_gate)
        {
            var key = TokenService.Hash(secret);
            if (!_pending.TryGetValue(key, out var invitation) || invitation.Expires <= Now
                || invitation.DeviceId != deviceId || invitation.Address != Address(address!)) return false;
            return _pending.Remove(key);
        }
    }
}

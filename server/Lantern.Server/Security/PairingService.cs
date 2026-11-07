using System.Collections.Concurrent;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Security;

/// <summary>
/// The pairing handshake: an unpaired client asks, the PC user approves, the client polls
/// and receives its bearer token exactly once. Requests live in memory because a pairing
/// attempt is worthless after a restart.
/// </summary>
public sealed class PairingService
{
    /// <summary>
    /// How long an unanswered request stays valid. Deliberately short: the PIN is shown on the
    /// projector and retyped on the PC, so it only has to survive the walk between the two.
    /// </summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PurgeAfter = RequestLifetime + RequestLifetime;

    private readonly DeviceRepository _devices;
    private readonly ILogger<PairingService> _logger;
    private readonly ConcurrentDictionary<string, PairingRequest> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrustedDevice> _ephemeral = new(StringComparer.OrdinalIgnoreCase);

    public PairingService(DeviceRepository devices, ILogger<PairingService> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    /// <summary>Pending requests, newest first (what the PC approval dialog shows).</summary>
    public IReadOnlyList<PairingRequest> Pending()
    {
        Purge();
        return _requests.Values
            .Where(r => r.Status == PairingStatus.Pending)
            .OrderByDescending(r => r.CreatedUtc)
            .ToList();
    }

    public PairingRequest? Find(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return null;
        Purge();
        return _requests.TryGetValue(requestId.Trim(), out var request) ? request : null;
    }

    /// <summary>Creates a request, or returns the one the device already has pending.</summary>
    public PairingRequest Create(PairRequestDto request, string? ipAddress)
    {
        Purge();

        var existing = _requests.Values.FirstOrDefault(r =>
            r.Status == PairingStatus.Pending
            && r.PollSecretHash == TokenService.Hash(request.PollSecret ?? "")
            && string.Equals(r.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IpAddress = ipAddress;
            return existing;
        }

        var requestId = CreateRequestId();
        var created = new PairingRequest
        {
            RequestId = requestId,
            PollSecretHash = TokenService.Hash(request.PollSecret ?? ""),
            DeviceId = request.DeviceId.Trim(),
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Unknown device" : request.Name.Trim(),
            Model = request.Model,
            Manufacturer = request.Manufacturer,
            Platform = request.Platform,
            IpAddress = ipAddress,
        };

        if (request.Capabilities is not null)
        {
            _capabilities[requestId] = ApiJson.Serialize(request.Capabilities);
        }

        _requests[requestId] = created;
        _logger.LogInformation(
            "Pairing requested by {Name} ({DeviceId}) at {Ip}",
            created.Name, created.DeviceId, ipAddress ?? "unknown");
        return created;
    }

    /// <summary>Issues the token after approval. Null when the request is unknown.</summary>
    public async Task<PairingRequest?> ApproveAsync(
        string? requestId, bool trust = true, string? reason = null, CancellationToken ct = default)
    {
        var request = Find(requestId);
        if (request is null) return null;
        if (request.Status == PairingStatus.Approved) return request;
        if (request.Status != PairingStatus.Pending) return null;

        var token = TokenService.CreateToken();
        var hash = TokenService.Hash(token);
        _capabilities.TryGetValue(request.RequestId, out var capabilitiesJson);

        var device = new TrustedDevice
        {
            DeviceId = request.DeviceId,
            Name = request.Name,
            Model = request.Model,
            Manufacturer = request.Manufacturer,
            Platform = request.Platform,
            TokenHash = hash,
            ApprovedUtc = DateTime.UtcNow,
            CapabilitiesJson = capabilitiesJson,
        };

        if (trust)
        {
            await _devices.UpsertAsync(device, ct).ConfigureAwait(false);
        }
        else
        {
            // "Do not remember": the token stays valid until the server restarts.
            _ephemeral[hash] = device;
        }

        request.Status = PairingStatus.Approved;
        request.IssuedToken = token;
        request.RejectReason = reason;
        _logger.LogInformation(
            "Pairing approved for {Name} (remembered: {Trust})",
            request.Name, trust);
        return request;
    }

    public PairingRequest? Reject(string? requestId, string? reason)
    {
        var request = Find(requestId);
        if (request is null) return null;
        if (request.Status == PairingStatus.Approved) return request;
        if (request.Status != PairingStatus.Pending) return null;

        request.Status = PairingStatus.Rejected;
        request.RejectReason = reason;
        _logger.LogInformation("Pairing rejected for {Name}", request.Name);
        return request;
    }

    /// <summary>
    /// Hands the token to the polling client. It is returned exactly once; afterwards the
    /// client only sees "approved" and must keep the token it already received.
    /// </summary>
    public string? ConsumeToken(PairingRequest request)
    {
        lock (request) { var token = request.IssuedToken; request.IssuedToken = null; return token; }
    }

    /// <summary>Device behind a "do not remember" token, if any.</summary>
    public TrustedDevice? ResolveEphemeralToken(string tokenHash) =>
        tokenHash.Length == 0 ? null : _ephemeral.TryGetValue(tokenHash, out var device) ? device : null;

    public int ExpiresInSeconds(PairingRequest request)
    {
        var remaining = RequestLifetime - (DateTime.UtcNow - request.CreatedUtc);
        return remaining <= TimeSpan.Zero ? 0 : (int)remaining.TotalSeconds;
    }

    /// <summary>Marks stale requests as expired and forgets very old ones.</summary>
    public void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, request) in _requests)
        {
            var age = now - request.CreatedUtc;
            if (age > PurgeAfter)
            {
                _requests.TryRemove(id, out _);
                _capabilities.TryRemove(id, out _);
                continue;
            }

            if (request.Status == PairingStatus.Pending && age > RequestLifetime)
            {
                request.Status = PairingStatus.Expired;
            }
        }
    }

    /// <summary>
    /// The request id is the PIN the projector shows: the PC user types it to approve this
    /// device, which is why it has to stay short and human typeable.
    /// </summary>
    private string CreateRequestId()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = TokenService.CreatePin();
            if (!_requests.ContainsKey(candidate)) return candidate;
        }

        return Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }
}

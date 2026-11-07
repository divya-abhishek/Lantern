namespace Lantern.Core.Models;

/// <summary>A device the user explicitly approved ("pairing").</summary>
public sealed class TrustedDevice
{
    public long Id { get; set; }

    /// <summary>Stable client-generated id (Android: Settings.Secure.ANDROID_ID based).</summary>
    public required string DeviceId { get; set; }

    /// <summary>Friendly name shown on the PC ("E GATE Atom 3X").</summary>
    public required string Name { get; set; }

    public string? Model { get; set; }
    public string? Manufacturer { get; set; }
    public string? Platform { get; set; }

    /// <summary>SHA-256 hash of the issued token (the raw token never touches the DB).</summary>
    public required string TokenHash { get; set; }

    public DateTime ApprovedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Revoked { get; set; }

    /// <summary>Serialized <see cref="Lantern.Core.Models.DeviceCapabilities"/> for the codec decision engine.</summary>
    public string? CapabilitiesJson { get; set; }

    public bool IsActive => !Revoked;
}

/// <summary>A pending pairing request that requires user approval on the PC.</summary>
public sealed class PairingRequest
{
    public string PollSecretHash { get; init; } = "";
    public required string RequestId { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? Platform { get; init; }
    public string? IpAddress { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public PairingStatus Status { get; set; } = PairingStatus.Pending;

    /// <summary>Issued token, only present in memory until the client polls it once.</summary>
    public string? IssuedToken { get; set; }

    public string? RejectReason { get; set; }
}

public enum PairingStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
}

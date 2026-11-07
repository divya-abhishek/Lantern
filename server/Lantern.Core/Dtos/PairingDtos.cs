namespace Lantern.Core.Dtos;

using Lantern.Core.Models;

/// <summary>POST /api/v1/pair/request – sent by an unpaired client.</summary>
public sealed class PairRequestDto
{
    public string? PollSecret { get; set; }
    public string? InvitationSecret { get; set; }
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public string? Model { get; set; }
    public string? Manufacturer { get; set; }
    public string? Platform { get; set; }
    public DeviceCapabilities? Capabilities { get; set; }
}

/// <summary>GET /api/v1/pair/status/{requestId} – polled until approved.</summary>
public sealed class PairStatusDto
{
    public required string RequestId { get; set; }
    public PairingStatus Status { get; set; }

    /// <summary>Only returned once, when the request has been approved.</summary>
    public string? Token { get; set; }

    public string? Message { get; set; }
    public string? ServerName { get; set; }
    public int Port { get; set; }
    public int PollIntervalMs { get; set; } = 1500;
    public int ExpiresInSeconds { get; set; }
}

/// <summary>POST /api/v1/pair/decision – called by the PC UI (in-process).</summary>
public sealed class PairDecisionDto
{
    public required string RequestId { get; set; }
    public bool Approve { get; set; }
    public string? Reason { get; set; }

    /// <summary>Optional: remember the device as trusted (default true when approving).</summary>
    public bool Trust { get; set; } = true;
}

/// <summary>A trusted device as listed in the PC UI.</summary>
public sealed class TrustedDeviceDto
{
    public long Id { get; set; }
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public string? Model { get; set; }
    public string? Platform { get; set; }
    public DateTime ApprovedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Revoked { get; set; }
    public bool Online { get; set; }
    public string? CapabilitiesSummary { get; set; }
}

/// <summary>POST /api/v1/devices/capabilities – the client reports decoders at runtime.</summary>
public sealed class CapabilityReportDto
{
    public required DeviceCapabilities Capabilities { get; set; }
    public double? MeasuredThroughputMbps { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>Result of GET /api/v1/auth/check.</summary>
public sealed class AuthCheckDto
{
    public bool Authorized { get; set; }
    public required string DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public string? Message { get; set; }
}

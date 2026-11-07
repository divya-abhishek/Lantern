using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Server.Security;
using Lantern.Server.Services;

namespace Lantern.Server.Endpoints;

/// <summary>
/// The pairing handshake plus the device list the PC UI manages. The client polls
/// <see cref="ApiRoutes.PairingStatus"/> until the user approves it on the PC and receives
/// its bearer token exactly once.
/// </summary>
public static class PairingEndpoints
{
    private const string StatusRoute = ApiRoutes.Prefix + "/pairing/status/{requestId}";
    private const string ApproveRoute = ApiRoutes.Prefix + "/pairing/approve/{requestId}";
    private const string RejectRoute = ApiRoutes.Prefix + "/pairing/reject/{requestId}";
    private const string DeviceRoute = ApiRoutes.Devices + "/{id:long}";
    private const string RevokeRoute = ApiRoutes.Devices + "/{id:long}/revoke";

    /// <summary>What the PC approval dialog lists (never contains a token).</summary>
    public sealed record PendingPairingDto(
        string RequestId,
        string DeviceId,
        string Name,
        string? Model,
        string? Platform,
        string? IpAddress,
        DateTime CreatedUtc,
        int ExpiresInSeconds);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.PairingRequest, RequestAsync).AllowLanternAnonymous();
        app.MapGet(StatusRoute, StatusAsync).AllowLanternAnonymous();
        app.MapGet(ApiRoutes.PairingRequests, PendingAsync).RequireLanternAdmin();
        app.MapPost(ApproveRoute, ApproveAsync).RequireLanternAdmin();
        app.MapPost(RejectRoute, RejectAsync).RequireLanternAdmin();
        app.MapPost(ApiRoutes.Devices + "/receivers/{deviceId}/connect", InviteAsync).RequireLanternAdmin();

        app.MapGet(ApiRoutes.Devices, DevicesAsync).RequireLanternAuth();
        app.MapGet(ApiRoutes.Devices + "/receivers", (DiscoveryHostedService discovery) =>
            ApiResults.Ok(discovery.Receivers.Snapshot())).RequireLanternAdmin();
        app.MapPost(ApiRoutes.DeviceCapabilities, CapabilitiesAsync).RequireLanternAuth();
        app.MapGet(DeviceRoute, DeviceAsync).RequireLanternAuth();
        app.MapPost(RevokeRoute, RevokeAsync).RequireLanternAdmin();
    }

    /// <summary>POST /api/v1/pairing/request - an unpaired client introduces itself.</summary>
    private static async Task<IResult> RequestAsync(
        HttpContext http, PairingService pairing, ServerRuntime runtime, ReceiverInvitations invitations, CancellationToken ct)
    {
        var request = await ApiResults.BodyAsync<PairRequestDto>(http, ct).ConfigureAwait(false);
        if (request is null
            || string.IsNullOrWhiteSpace(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.Name))
        {
            return ApiResults.BadRequest("deviceId and name are required.");
        }

        if (request.PollSecret is not { Length: >= 32 and <= 128 })
            return ApiResults.BadRequest("Update the receiver: a pairing poll secret is required.");
        if (pairing.Pending().Count(r => r.IpAddress == http.Connection.RemoteIpAddress?.ToString()) >= 8)
            return ApiResults.Error(429, "pairing_limit", "Too many pending pairing requests. Wait two minutes.");
        var address = http.Connection.RemoteIpAddress?.ToString();
        if (request.InvitationSecret is { } secret && !invitations.Consume(secret, request.DeviceId, address))
            return ApiResults.Forbidden("The connection request expired or belongs to another receiver. Click Connect on the PC again.");
        var created = pairing.Create(request, address);
        if (request.InvitationSecret is not null)
            await pairing.ApproveAsync(created.RequestId, trust: true, ct: ct).ConfigureAwait(false);
        return ApiResults.Accepted(Status(created, pairing, runtime, token: null));
    }

    private static async Task<IResult> InviteAsync(string deviceId, DiscoveryHostedService discovery,
        ReceiverInvitations invitations, ServerRuntime runtime, CancellationToken ct)
    {
        var receiver = discovery.Receivers.Snapshot().FirstOrDefault(r => r.DeviceId == deviceId);
        if (receiver is null) return ApiResults.NotFound("Receiver is offline. Open Lantern on it and try again.");
        if (!receiver.SupportsConnect) return ApiResults.BadRequest("Update Lantern on the receiver to enable tap-to-connect. PIN pairing is still available.");
        var secret = invitations.Create(deviceId, receiver.Address);
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {
            protocol = "lantern", version = 1, kind = "connect", deviceId,
            serverName = runtime.Settings.Current.ServerName, port = runtime.Settings.Current.Port,
            invitation = secret
        });
        using var udp = new System.Net.Sockets.UdpClient();
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(receiver.Address), 8788);
        // Duplicates are ignored by Android. A small retry tolerates lost Wi-Fi datagrams.
        for (var i = 0; i < 3; i++) {
            await udp.SendAsync(payload, endpoint, ct);
            if (i < 2) await Task.Delay(150, ct);
        }
        return ApiResults.Ok(new { message = $"Confirm Yes on {receiver.Name}. The request expires in one minute." });
    }

    /// <summary>GET /api/v1/pairing/status/{requestId} - polled until the PC decides.</summary>
    private static IResult StatusAsync(string requestId, HttpContext http, PairingService pairing, ServerRuntime runtime)
    {
        var request = pairing.Find(requestId);
        if (request is null)
        {
            return ApiResults.NotFound(
                "This pairing request is unknown or has already been forgotten.", "pairing_not_found");
        }

        var secret = http.Request.Headers["X-Lantern-Pairing"].ToString();
        if (secret.Length < 32 || TokenService.Hash(secret) != request.PollSecretHash)
            return ApiResults.Forbidden("This pairing request belongs to another receiver.");
        var token = pairing.ConsumeToken(request);
        return ApiResults.Ok(Status(request, pairing, runtime, token));
    }

    /// <summary>GET /api/v1/pairing/requests - what the PC UI shows in its approval dialog.</summary>
    private static IResult PendingAsync(PairingService pairing)
    {
        var pending = pairing.Pending()
            .Select(r => new PendingPairingDto(
                r.RequestId, r.DeviceId, r.Name, r.Model, r.Platform, r.IpAddress,
                r.CreatedUtc, pairing.ExpiresInSeconds(r)))
            .ToList();

        return ApiResults.Ok(pending);
    }

    private static async Task<IResult> ApproveAsync(
        string requestId, HttpContext http, PairingService pairing, ServerRuntime runtime, CancellationToken ct)
    {
        var decision = await ApiResults.BodyAsync<PairDecisionDto>(http, ct).ConfigureAwait(false);
        var request = await pairing
            .ApproveAsync(requestId, decision?.Trust ?? true, decision?.Reason, ct)
            .ConfigureAwait(false);

        if (request is null)
        {
            return ApiResults.NotFound("This pairing request is unknown or has expired.", "pairing_not_found");
        }

        return ApiResults.Ok(Status(request, pairing, runtime, token: null));
    }

    private static async Task<IResult> RejectAsync(
        string requestId, HttpContext http, PairingService pairing, ServerRuntime runtime, CancellationToken ct)
    {
        var decision = await ApiResults.BodyAsync<PairDecisionDto>(http, ct).ConfigureAwait(false);
        var reason = decision?.Reason ?? ApiResults.QueryText(http, "reason");
        var request = pairing.Reject(requestId, reason);

        if (request is null)
        {
            return ApiResults.NotFound("This pairing request is unknown or has expired.", "pairing_not_found");
        }

        return ApiResults.Ok(Status(request, pairing, runtime, token: null));
    }

    private static async Task<IResult> DevicesAsync(
        HttpContext http, DeviceRepository devices, ConnectionHub hub, CancellationToken ct)
    {
        var includeRevoked = ApiResults.QueryBool(http, "includeRevoked") ?? false;
        var rows = await devices.GetAllAsync(includeRevoked, ct).ConfigureAwait(false);
        var list = rows.Select(d => DtoMapper.Device(d, hub.IsOnline(d.DeviceId))).ToList();
        return ApiResults.Ok(list);
    }

    /// <summary>POST /api/v1/devices/capabilities - the decoders this client actually has.</summary>
    private static async Task<IResult> CapabilitiesAsync(
        HttpContext http, DeviceRepository devices, SettingsStore settings, CancellationToken ct)
    {
        var report = await ApiResults.BodyAsync<CapabilityReportDto>(http, ct).ConfigureAwait(false);
        if (report is null) return ApiResults.BadRequest("A CapabilityReportDto body is required.");

        var auth = http.Auth();
        var deviceId = string.IsNullOrWhiteSpace(auth.DeviceId)
            ? report.Capabilities.DeviceId
            : auth.DeviceId;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return ApiResults.BadRequest("The capability report must carry a deviceId.");
        }

        report.Capabilities.DeviceId = deviceId;
        if (report.MeasuredThroughputMbps is { } measured && measured > 0)
        {
            report.Capabilities.MeasuredThroughputMbps = measured;
        }

        await devices
            .SaveCapabilitiesAsync(deviceId, ApiJson.Serialize(report.Capabilities), DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        if (report.MeasuredThroughputMbps is { } throughput && throughput > 0)
        {
            settings.SetMeasuredThroughput(throughput);
        }

        return ApiResults.NoContent();
    }

    private static async Task<IResult> DeviceAsync(
        long id, DeviceRepository devices, ConnectionHub hub, CancellationToken ct)
    {
        var device = (await devices.GetAllAsync(includeRevoked: true, ct).ConfigureAwait(false))
            .FirstOrDefault(d => d.Id == id);

        if (device is null) return ApiResults.NotFound($"Device {id} was not found.");
        return ApiResults.Ok(DtoMapper.Device(device, hub.IsOnline(device.DeviceId)));
    }

    private static async Task<IResult> RevokeAsync(
        long id, DeviceRepository devices, ConnectionHub hub, CancellationToken ct)
    {
        var device = (await devices.GetAllAsync(includeRevoked: true, ct).ConfigureAwait(false))
            .FirstOrDefault(d => d.Id == id);

        if (device is null) return ApiResults.NotFound($"Device {id} was not found.");

        await devices.SetRevokedAsync(id, true, ct).ConfigureAwait(false);
        await hub.CloseAsync(device.DeviceId, "This device was revoked on the PC.").ConfigureAwait(false);
        return ApiResults.NoContent();
    }

    private static PairStatusDto Status(
        PairingRequest request, PairingService pairing, ServerRuntime runtime, string? token)
    {
        var settings = runtime.Settings.Current;

        return new PairStatusDto
        {
            RequestId = request.RequestId,
            Status = request.Status,
            Token = token,
            Message = request.Status switch
            {
                PairingStatus.Pending => $"Waiting for approval on {settings.ServerName}.",
                PairingStatus.Approved => "Paired.",
                PairingStatus.Rejected => request.RejectReason ?? "The request was rejected on the PC.",
                _ => "The request expired. Start pairing again.",
            },
            ServerName = settings.ServerName,
            Port = settings.Port,
            PollIntervalMs = 1500,
            ExpiresInSeconds = pairing.ExpiresInSeconds(request),
        };
    }
}

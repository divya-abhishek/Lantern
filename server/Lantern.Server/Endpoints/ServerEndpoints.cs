using System.Security.Cryptography;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Media.Tooling;
using Lantern.Media.Transcoding;
using Lantern.Network.Throughput;
using Lantern.Server.Security;
using Lantern.Server.Services;

namespace Lantern.Server.Endpoints;

/// <summary>
/// Server identity, the client settings subset, diagnostics, the throughput payload and
/// the WebSocket hub endpoint (<see cref="ApiRoutes.WebSocket"/>).
/// </summary>
public static class ServerEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.ServerInfo, InfoAsync).AllowLanternAnonymous();
        app.MapGet(ApiRoutes.ServerSettings, SettingsAsync).RequireLanternAdmin();
        app.MapGet(ApiRoutes.AuthCheck, AuthCheckAsync).AllowLanternAnonymous();
        app.MapGet(ApiRoutes.Diagnostics, DiagnosticsAsync).RequireLanternAdmin();
        app.MapGet(ApiRoutes.SpeedTestPayload, PayloadAsync).RequireLanternAuth();
        app.Map(ApiRoutes.WebSocket, WebSocketAsync).RequireLanternAuth();
    }

    /// <summary>GET /api/v1/server/info - the handshake a client does before pairing.</summary>
    private static async Task<IResult> InfoAsync(
        HttpContext http,
        ServerRuntime runtime,
        SettingsStore settingsStore,
        MediaRepository media,
        FfmpegLocator locator,
        DiscoveryHostedService discovery,
        CancellationToken ct)
    {
        var settings = settingsStore.Current;

        // Resolved once on startup; retried here when ffmpeg was installed afterwards.
        if (!runtime.Tools.Available && runtime.Tools.Error is null)
        {
            runtime.Tools = await locator.ResolveAsync(settings, forceRefresh: false, ct).ConfigureAwait(false);
        }

        var counts = await media.GetCountsAsync(ct).ConfigureAwait(false);

        return ApiResults.Ok(new ServerInfoDto
        {
            ServerId = runtime.Identity.ServerId,
            Name = settings.ServerName,
            Version = runtime.Version,
            ApiVersion = runtime.ApiVersion,
            MachineName = Environment.MachineName,
            Os = Environment.OSVersion.ToString(),
            Port = settings.Port,
            Addresses = [.. runtime.LanAddresses()],
            PrimaryAddress = runtime.PrimaryAddressFor(http.Connection.RemoteIpAddress),
            StartedUtc = runtime.StartedUtc,
            RequiresPairing = settings.RequirePairing,
            UptimeSeconds = runtime.UptimeSeconds,
            Features = new ServerFeaturesDto
            {
                Mdns = settings.EnableMdns && discovery.MdnsRunning,
                UdpBeacon = settings.EnableUdpBeacon && discovery.BeaconRunning,
                Transcoding = runtime.TranscodingAvailable,
                Thumbnails = settings.ThumbnailsEnabled && runtime.Tools.Available,
                Subtitles = true,
                PcAudio = settings.PcAudioEnabled,
                WebSocket = true,
                ResumeSync = true,
                StreamingProtocols = runtime.Tools.CanTranscode ? ["http-range", "hls"] : ["http-range"],
            },
            Library = counts,
            Ffmpeg = DtoMapper.Ffmpeg(runtime.Tools),
            MeasuredThroughputMbps = settings.LastMeasuredThroughputMbps > 0
                ? settings.LastMeasuredThroughputMbps
                : null,
        });
    }

    private static IResult SettingsAsync(ServerRuntime runtime, SettingsStore settingsStore) =>
        ApiResults.Ok(DtoMapper.ClientSettings(settingsStore.Current, runtime.Tools));

    /// <summary>GET /api/v1/auth/check - "am I allowed to talk to this server?".</summary>
    private static IResult AuthCheckAsync(HttpContext http, SettingsStore settingsStore)
    {
        var auth = http.Auth();
        var settings = settingsStore.Current;
        var authorized = auth.IsAdmin || auth.IsTrusted || !settings.RequirePairing;

        var message = authorized
            ? auth.IsTrusted
                ? "This device is paired."
                : auth.IsLocalAdmin
                    ? "Local access to the server machine."
                    : "This server does not require pairing."
            : "Approve this device in Lantern on the PC to start streaming.";

        return ApiResults.Ok(new AuthCheckDto
        {
            Authorized = authorized,
            DeviceId = auth.DeviceId,
            DeviceName = auth.DeviceName,
            Message = message,
        });
    }

    private static async Task<IResult> DiagnosticsAsync(
        HttpContext http,
        ServerRuntime runtime,
        SettingsStore settingsStore,
        MediaRepository media,
        ScanCoordinator scans,
        ConnectionHub hub,
        StreamRegistry streams,
        ProcessMonitor process,
        TranscodeSessionManager transcodes,
        LogBuffer logs,
        CancellationToken ct)
    {
        var settings = settingsStore.Current;
        var counts = await media.GetCountsAsync(ct).ConfigureAwait(false);
        var measured = settings.LastMeasuredThroughputMbps;
        var recommendation = measured > 0 ? ThroughputRecommendation.For(measured) : null;

        return ApiResults.Ok(new DiagnosticsDto
        {
            ServerId = runtime.Identity.ServerId,
            ServerName = settings.ServerName,
            Version = runtime.Version,
            StartedUtc = runtime.StartedUtc,
            UptimeSeconds = runtime.UptimeSeconds,
            ServerAddresses = [.. runtime.LanAddresses()],
            Port = settings.Port,
            ClientIp = http.Connection.RemoteIpAddress?.ToString(),
            ClientUserAgent = http.Request.Headers.UserAgent.ToString(),
            ConnectedClients = hub.Count,
            Clients = hub.Snapshot(),
            CpuPercent = process.Sample(),
            ProcessMemoryMb = process.ProcessMemoryMb,
            ManagedMemoryMb = process.ManagedMemoryMb,
            ThreadCount = process.ThreadCount,
            MeasuredThroughputMbps = measured > 0 ? measured : null,
            RecommendedStreamBitrateMbps = recommendation?.SuggestedBitrateKbps is { } kbps
                ? kbps / 1000.0
                : null,
            ThroughputSafetyFactor = settings.ThroughputSafetyFactor,
            Ffmpeg = DtoMapper.Ffmpeg(runtime.Tools),
            TranscodingSessions = transcodes.Snapshot(),
            ActiveStreams = streams.Snapshot(),
            Library = counts,
            Scanning = scans.Status,
            AudioMode = settings.PcAudioEnabled ? AudioMode.Pc : AudioMode.Projector,
            PcAudioAvailable = settings.PcAudioEnabled,
            PcAudioRunning = false,
            PcAudioOffsetMs = settings.PcAudioOffsetMs,
            RecentErrors = logs.Recent(25),
        });
    }

    /// <summary>
    /// GET /api/v1/speedtest/payload?bytes=N - incompressible bytes the client downloads to
    /// measure the real LAN rate (see <see cref="ThroughputProbe"/>).
    /// </summary>
    private static async Task<IResult> PayloadAsync(HttpContext http, CancellationToken ct)
    {
        var asked = ApiResults.QueryInt(http, "bytes", 4 * 1024 * 1024);
        var total = (long)Math.Clamp(asked, 64 * 1024, 64 * 1024 * 1024);

        http.Response.ContentType = "application/octet-stream";
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Lantern-Bytes"] = total.ToString();

        var buffer = new byte[Math.Min(total, 256 * 1024)];
        var remaining = total;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
            await http.Response.Body.WriteAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
            remaining -= chunk;
        }

        return Results.Empty;
    }

    /// <summary>GET /api/v1/ws - the single socket a client keeps open for everything else.</summary>
    private static async Task WebSocketAsync(HttpContext http, ConnectionHub hub)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await ApiResults
                .BadRequest("This endpoint only accepts WebSocket connections.", "websocket_required")
                .ExecuteAsync(http)
                .ConfigureAwait(false);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await hub.HandleAsync(http, http.Auth(), socket, http.RequestAborted).ConfigureAwait(false);
    }
}

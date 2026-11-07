namespace Lantern.Server.Endpoints;

using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Server.Security;
using Lantern.Server.Services;

/// <summary>
/// Screen mirroring: the control surface the PC UI drives, and the endpoint that tells a paired device
/// where to find the live stream.
///
/// The transport is RTSP into a MediaMTX relay that this server starts, owns, and stops with the
/// session. That is a deliberate change of shape: the old transport was MPEG-TS written into a chunked
/// HTTP response, which made this server the relay - framing, per-receiver queues, stale-data trimming -
/// and the parts it got wrong were exactly the parts that showed on the projector. RTSP is a real
/// broadcast protocol with an implementation that exists to be a relay, Media3 has a first-class client
/// for it, and the picture now reaches the projector from the live edge instead of from a queue this
/// process has to keep correct.
///
/// What did not change: every endpoint here still requires a trusted device or the loopback caller.
/// Mirroring this PC's screen is never something an anonymous LAN client gets to ask for, and the RTSP
/// URL - which carries this session's own random credential - is only ever handed out over this
/// authenticated API.
/// </summary>
public static class MirrorEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.MirrorCapabilities, CapabilitiesAsync).RequireLanternAuth();
        app.MapGet(ApiRoutes.MirrorStatus, StatusAsync).RequireLanternAuth();
        app.MapPost(ApiRoutes.MirrorStart, StartAsync).RequireLanternAdmin();
        app.MapPost(ApiRoutes.MirrorStop, StopAsync).RequireLanternAdmin();
    }

    /// <summary>GET /mirror/capabilities - displays, backends, verified encoders, audio and the relay.</summary>
    private static async Task<IResult> CapabilitiesAsync(
        HttpContext http, MirrorService mirror, CancellationToken ct)
    {
        var refresh = ApiResults.QueryBool(http, "refresh") ?? false;
        return ApiResults.Ok(await mirror.CapabilitiesAsync(refresh, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// GET /mirror/status - the live numbers (encoder, bitrate, measured latency, receivers) and, while a
    /// session is running, the RTSP URL this caller should open. The URL is built for the address the
    /// call came in on, so a machine with more than one interface answers each device with one it can
    /// actually reach.
    /// </summary>
    private static async Task<IResult> StatusAsync(
        HttpContext http, MirrorService mirror, CancellationToken ct)
    {
        var auth = http.Auth();
        if (!auth.IsLocalAdmin && !auth.IsTrusted)
            return ApiResults.Unauthorized("Pair this receiver before watching the PC screen.");
        return ApiResults.Ok(await mirror
            .StatusAsync(http.Connection.RemoteIpAddress, ct)
            .ConfigureAwait(false));
    }

    /// <summary>POST /mirror/start - begin capturing. Body is a MirrorSettingsDto; empty means defaults.</summary>
    private static async Task<IResult> StartAsync(
        HttpContext http, MirrorService mirror, CancellationToken ct)
    {
        var settings = await ApiResults.BodyAsync<MirrorSettingsDto>(http, ct).ConfigureAwait(false)
                       ?? new MirrorSettingsDto();

        var error = await mirror.StartAsync(settings, ct).ConfigureAwait(false);

        return error is null
            ? ApiResults.Ok(await mirror
                .StatusAsync(http.Connection.RemoteIpAddress, ct)
                .ConfigureAwait(false))
            : ApiResults.Unavailable(error, "mirror_unavailable");
    }

    /// <summary>POST /mirror/stop - release the capture, the encoder, the audio device and the relay.</summary>
    private static async Task<IResult> StopAsync(MirrorService mirror)
    {
        var stopped = await mirror.StopAsync().ConfigureAwait(false);
        return ApiResults.Ok(new MirrorStopDto { Stopped = stopped, State = MirrorState.Idle });
    }
}


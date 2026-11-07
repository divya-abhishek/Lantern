using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Database.Repositories;
using Lantern.Server.Security;
using Lantern.Server.Services;

namespace Lantern.Server.Endpoints;

/// <summary>Playback planning, resume reporting and the continue-watching shelf.</summary>
public static class PlaybackEndpoints
{
    private const string ProgressRoute = ApiRoutes.Prefix + "/playback/{mediaId:long}/progress";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.PlaybackPlan, PlanAsync).RequireLanternAuth();
        app.MapPost(ProgressRoute, ReportAsync).RequireLanternAuth();
        app.MapGet(ApiRoutes.PlaybackContinue, ContinueAsync).RequireLanternAuth();
    }

    /// <summary>POST /api/v1/playback/plan - "how should I play this?".</summary>
    private static async Task<IResult> PlanAsync(
        HttpContext http, PlaybackPlanner planner, CancellationToken ct)
    {
        var request = await ApiResults.BodyAsync<PlaybackPlanRequestDto>(http, ct).ConfigureAwait(false);
        if (request is null) return ApiResults.BadRequest("A PlaybackPlanRequestDto body is required.");

        // The device id comes from the paired token; ?deviceId= lets the PC test another profile.
        var deviceId = (http.Auth().IsLocalAdmin ? ApiResults.QueryText(http, "deviceId") : null) ?? http.Auth().DeviceId;

        var (outcome, failure) = await planner.PlanAsync(request, deviceId, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return ApiResults.Error(failure.Status, failure.Code, failure.Message, failure.Detail);
        }

        return ApiResults.Ok(outcome!.Dto);
    }

    /// <summary>POST /api/v1/playback/{mediaId}/progress - resume heartbeat.</summary>
    private static async Task<IResult> ReportAsync(
        long mediaId, HttpContext http, ProgressService progress, DeviceRepository devices, Lantern.Media.Transcoding.TranscodeSessionManager transcodes, CancellationToken ct)
    {
        var report = await ApiResults.BodyAsync<ProgressReportDto>(http, ct).ConfigureAwait(false);
        if (report is null) return ApiResults.BadRequest("A ProgressReportDto body is required.");

        // The route is authoritative so a stale body cannot write to another item.
        if (mediaId > 0) report.MediaId = mediaId;

        var auth = http.Auth();
        foreach (var session in transcodes.Sessions.Where(s => s.MediaId == mediaId && s.ClientId == auth.DeviceId)) session.Touch();
        var ack = await progress.ReportAsync(report, auth.DeviceId, ct).ConfigureAwait(false);
        if (ack is null) return ApiResults.NotFound($"Media {report.MediaId} was not found.");

        if (auth.IsTrusted)
        {
            await devices.TouchLastSeenAsync(auth.DeviceId, ct).ConfigureAwait(false);
        }

        return ApiResults.Ok(ack);
    }

    /// <summary>GET /api/v1/playback/continue - the resume shelf for this device.</summary>
    private static async Task<IResult> ContinueAsync(
        HttpContext http, ProgressService progress, CancellationToken ct)
    {
        var limit = Math.Clamp(ApiResults.QueryInt(http, "limit", 20), 1, 100);
        var shelf = await progress.ContinueAsync(http.Auth().DeviceId, limit, ct).ConfigureAwait(false);
        return ApiResults.Ok(shelf);
    }
}

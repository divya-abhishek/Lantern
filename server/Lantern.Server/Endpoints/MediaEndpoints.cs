using System.Globalization;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Media.Probing;
using Lantern.Media.Thumbnails;
using Lantern.Server.Security;
using Lantern.Server.Services;

namespace Lantern.Server.Endpoints;

/// <summary>
/// Browsing (list/detail), subtitle delivery, thumbnails and the forced ffprobe refresh.
/// The bytes of a movie itself are served by <see cref="StreamingEndpoints"/>.
/// </summary>
public static class MediaEndpoints
{
    private const string ItemRoute = ApiRoutes.Media + "/{mediaId:long}";
    private const string SubtitleListRoute = ApiRoutes.Media + "/{mediaId:long}/subtitles";
    private const string SubtitleVttRoute = ApiRoutes.Media + "/{mediaId:long}/subtitles/{subtitleId}.vtt";
    private const string ThumbnailRoute = ApiRoutes.Media + "/{mediaId:long}/thumbnail.jpg";
    private const string ProbeRoute = ApiRoutes.Media + "/{mediaId:long}/probe";

    /// <summary>One page of the library grid plus the counters the grid needs to page.</summary>
    public sealed record MediaListDto(
        IReadOnlyList<MediaSummaryDto> Items,
        int Total,
        int Limit,
        int Offset,
        bool HasMore,
        MediaSortOrder Sort,
        MediaKind? Kind,
        string? Search);

    /// <summary>The refreshed item as the next plan will see it.</summary>
    public sealed record ProbeResultDto(
        long MediaId,
        long FileId,
        string FileName,
        bool Succeeded,
        string? Error,
        MediaDetailDto Media);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Media, ListAsync).RequireLanternAuth();
        app.MapGet(ItemRoute, DetailAsync).RequireLanternAuth();
        app.MapGet(SubtitleListRoute, SubtitlesAsync).RequireLanternAuth();
        app.MapGet(SubtitleVttRoute, SubtitleVttAsync).RequireLanternAuth();
        app.MapGet(ThumbnailRoute, ThumbnailAsync).RequireLanternAuth();
        app.MapPost(ProbeRoute, ProbeAsync).RequireLanternAuth();
    }

    /// <summary>
    /// GET /api/v1/media - the library grid. Filters: <c>query</c> (aliases q/search),
    /// <c>kind</c>, <c>folderId</c>, <c>show</c>, <c>season</c>, <c>watched</c>, <c>hdr</c>,
    /// <c>minYear</c>/<c>maxYear</c>; paging: <c>limit</c>/<c>offset</c>; ordering: <c>sort</c>.
    /// </summary>
    private static async Task<IResult> ListAsync(
        HttpContext http, MediaRepository media, CancellationToken ct)
    {
        var limit = Math.Clamp(ApiResults.QueryInt(http, "limit", 60), 1, MediaQuery.MaxLimit);
        var offset = Math.Max(0, ApiResults.QueryInt(http, "offset", 0));
        var kind = ParseKind(ApiResults.QueryText(http, "kind"));
        var sort = ParseSort(ApiResults.QueryText(http, "sort"));

        // ?query= is the documented name; q/search are the aliases clients type by accident.
        var search = ApiResults.QueryText(http, "query")
            ?? ApiResults.QueryText(http, "q")
            ?? ApiResults.QueryText(http, "search");

        var query = new MediaQuery
        {
            Search = search,
            Kind = kind,
            FolderId = ApiResults.QueryLong(http, "folderId"),
            ShowTitle = ApiResults.QueryText(http, "show"),
            SeasonNumber = Positive(ApiResults.QueryInt(http, "season", 0)),
            Watched = ApiResults.QueryBool(http, "watched"),
            Hdr = ApiResults.QueryBool(http, "hdr"),
            MinYear = Positive(ApiResults.QueryInt(http, "minYear", 0)),
            MaxYear = Positive(ApiResults.QueryInt(http, "maxYear", 0)),
            Limit = limit,
            Offset = offset,
            Sort = sort,
            IncludeFiles = true,
        };

        var page = await media.GetPageAsync(query, ct).ConfigureAwait(false);
        var items = page.Items.Select(item => DtoMapper.Summary(item, item.PrimaryFile)).ToList();

        return ApiResults.Ok(new MediaListDto(
            items, page.Total, limit, offset, offset + items.Count < page.Total, sort, kind, search));
    }

    /// <summary>
    /// GET /api/v1/media/{mediaId} - the detail screen: tracks, subtitles, resume position and
    /// (for episodes) the next episode. Add <c>?plan=true</c> to also get the playback plan.
    /// </summary>
    private static async Task<IResult> DetailAsync(
        long mediaId,
        HttpContext http,
        MediaRepository media,
        ProgressRepository progress,
        PlaybackPlanner planner,
        CancellationToken ct)
    {
        var item = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return ApiResults.NotFound($"Media {mediaId} was not found.");

        // The device id comes from the paired token; ?deviceId= lets the PC preview another profile.
        var deviceId = (http.Auth().IsLocalAdmin ? ApiResults.QueryText(http, "deviceId") : null) ?? http.Auth().DeviceId;

        var resume = await progress.GetAsync(mediaId, deviceId, ct).ConfigureAwait(false);
        var device = await planner.ResolveDeviceAsync(deviceId, ct).ConfigureAwait(false);
        var detail = DtoMapper.Detail(item, resume, device);

        if (item.Kind == MediaKind.Episode && !string.IsNullOrWhiteSpace(item.ShowTitle))
        {
            var next = await NextEpisodeAsync(media, item, ct).ConfigureAwait(false);
            detail.NextEpisode = next is null ? null : DtoMapper.Summary(next, next.PrimaryFile);
        }

        if (ApiResults.QueryBool(http, "plan") ?? false)
        {
            var request = new PlaybackPlanRequestDto
            {
                MediaId = item.Id,
                Quality = ParseQuality(ApiResults.QueryText(http, "quality")),
                AudioOnly = ApiResults.QueryBool(http, "audioOnly") ?? false,
                AudioTrackIndex = Positive(ApiResults.QueryInt(http, "audioTrack", 0)),
                StartSeconds = ParseSeconds(ApiResults.QueryText(http, "start")),
            };

            var (outcome, failure) = await planner.PlanAsync(request, deviceId, ct, prepare: false).ConfigureAwait(false);
            if (failure is not null)
            {
                return ApiResults.Error(failure.Status, failure.Code, failure.Message, failure.Detail);
            }

            detail.Plan = outcome!.Dto;
        }

        return ApiResults.Ok(detail);
    }

    /// <summary>The episode that follows this one in the same show, in season/episode order.</summary>
    private static async Task<MediaItem?> NextEpisodeAsync(
        MediaRepository media, MediaItem item, CancellationToken ct)
    {
        var page = await media.GetPageAsync(
            new MediaQuery
            {
                Kind = MediaKind.Episode,
                ShowTitle = item.ShowTitle,
                Limit = 500,
                IncludeFiles = true,
            },
            ct).ConfigureAwait(false);

        return page.Items
            .Where(candidate => candidate.Id != item.Id && PlaysAfter(candidate, item))
            .OrderBy(candidate => candidate.SeasonNumber ?? 0)
            .ThenBy(candidate => candidate.EpisodeNumber ?? 0)
            .FirstOrDefault();
    }

    private static bool PlaysAfter(MediaItem candidate, MediaItem current)
    {
        var season = candidate.SeasonNumber ?? 0;
        var currentSeason = current.SeasonNumber ?? 0;
        return season != currentSeason
            ? season > currentSeason
            : (candidate.EpisodeNumber ?? 0) > (current.EpisodeNumber ?? 0);
    }

    /// <summary>
    /// GET /api/v1/media/{mediaId}/subtitles - every external sibling file and embedded stream,
    /// each with the WebVTT URL Media3 should open.
    /// </summary>
    private static async Task<IResult> SubtitlesAsync(
        long mediaId, HttpContext http, MediaRepository media, CancellationToken ct)
    {
        var item = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return ApiResults.NotFound($"Media {mediaId} was not found.");

        var file = ResolveFile(item, ApiResults.QueryLong(http, "fileId"));
        if (file is null) return ApiResults.NotFound($"Media {mediaId} has no playable file.");

        return ApiResults.Ok(file.SubtitleTracks.Select(track => DtoMapper.Subtitle(track, item.Id)).ToList());
    }

    /// <summary>
    /// GET /api/v1/media/{mediaId}/subtitles/{subtitleId}.vtt - the track as WebVTT. SRT/VTT
    /// sidecars are converted in process; ASS/SSA and embedded tracks go through ffmpeg.
    /// </summary>
    private static async Task<IResult> SubtitleVttAsync(
        long mediaId,
        string subtitleId,
        HttpContext http,
        MediaRepository media,
        SubtitleConverter converter,
        SettingsStore settings,
        CancellationToken ct)
    {
        var item = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return ApiResults.NotFound($"Media {mediaId} was not found.");

        var file = ResolveFile(item, ApiResults.QueryLong(http, "fileId"));
        if (file is null) return ApiResults.NotFound($"Media {mediaId} has no playable file.");

        var track = file.SubtitleTracks.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, subtitleId, StringComparison.OrdinalIgnoreCase));
        if (track is null)
        {
            return ApiResults.NotFound(
                $"Subtitle \"{subtitleId}\" is not part of this item.", "subtitle_not_found");
        }

        var vtt = await converter.GetWebVttAsync(file, track, settings.Current, ct).ConfigureAwait(false);
        if (vtt is null)
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                "subtitle_unavailable",
                $"\"{track.Label}\" could not be converted to WebVTT.",
                track.IsBitmapBased
                    ? "Image based subtitles (PGS/VobSub) need burn-in transcoding and are not served as text tracks."
                    : "ffmpeg is not available on this server to convert the track.");
        }

        http.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Text(vtt, "text/vtt; charset=utf-8");
    }

    /// <summary>
    /// GET /api/v1/media/{mediaId}/thumbnail.jpg - the cached poster frame. <c>?size=card</c>
    /// (default) / <c>detail</c> / <c>wide</c> picks the width, <c>?at=0.35</c> the frame
    /// position. ffmpeg generates the JPEG once and the cache serves every later request.
    /// </summary>
    private static async Task<IResult> ThumbnailAsync(
        long mediaId,
        HttpContext http,
        MediaRepository media,
        ThumbnailService thumbnails,
        SettingsStore settings,
        CancellationToken ct)
    {
        var item = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return ApiResults.NotFound($"Media {mediaId} was not found.");

        var file = ResolveFile(item, ApiResults.QueryLong(http, "fileId"));
        if (file is null) return ApiResults.NotFound($"Media {mediaId} has no playable file.");

        var path = await thumbnails
            .GetAsync(
                file,
                settings.Current,
                ParseThumbnailSize(ApiResults.QueryText(http, "size")),
                ParsePercent(http),
                ct)
            .ConfigureAwait(false);

        if (path is null || !File.Exists(path))
        {
            return ApiResults.Error(
                StatusCodes.Status404NotFound,
                "thumbnail_unavailable",
                "No thumbnail is available for this item.",
                "Thumbnails need ffmpeg; the file may also be missing or still being scanned.");
        }

        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(path, "image/jpeg", enableRangeProcessing: false);
    }

    /// <summary>
    /// POST /api/v1/media/{mediaId}/probe - re-reads the file with ffprobe and stores the result,
    /// so stale metadata can be fixed without rescanning the whole library.
    /// </summary>
    private static async Task<IResult> ProbeAsync(
        long mediaId,
        HttpContext http,
        MediaRepository media,
        ProgressRepository progress,
        PlaybackPlanner planner,
        FfprobeRunner prober,
        SettingsStore settings,
        CancellationToken ct)
    {
        var item = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return ApiResults.NotFound($"Media {mediaId} was not found.");

        var file = ResolveFile(item, ApiResults.QueryLong(http, "fileId"));
        if (file is null) return ApiResults.NotFound($"Media {mediaId} has no playable file.");

        if (!File.Exists(file.Path))
        {
            return ApiResults.NotFound(
                $"\"{file.FileName}\" is no longer on disk. Rescan the library.", "file_missing");
        }

        var probe = await prober.ProbeAsync(file.Path, settings.Current, ct).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                "probe_failed",
                $"ffprobe could not read \"{file.FileName}\".",
                probe.Error);
        }

        var record = probe.ToRecord(item.Id, file.Path, File.GetLastWriteTimeUtc(file.Path), DateTime.UtcNow);
        record.Id = file.Id;
        await media.UpsertFileAsync(record, ct).ConfigureAwait(false);

        // Re-read so the response carries exactly what the next plan will use.
        var refreshed = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false) ?? item;
        var deviceId = (http.Auth().IsLocalAdmin ? ApiResults.QueryText(http, "deviceId") : null) ?? http.Auth().DeviceId;

        var detail = DtoMapper.Detail(
            refreshed,
            await progress.GetAsync(mediaId, deviceId, ct).ConfigureAwait(false),
            await planner.ResolveDeviceAsync(deviceId, ct).ConfigureAwait(false));

        return ApiResults.Ok(new ProbeResultDto(mediaId, record.Id, record.FileName, true, null, detail));
    }

    /// <summary>?kind=movie|episode|other (aliases accepted so the TV grid can pass its tabs).</summary>
    private static MediaKind? ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => null,
        "movie" or "movies" or "film" or "films" => MediaKind.Movie,
        "episode" or "episodes" or "tv" or "show" or "shows" or "series" => MediaKind.Episode,
        "other" => MediaKind.Other,
        _ => null,
    };

    /// <summary>?sort=added|name|year|duration|size|played (the PC grid and the TV grid share it).</summary>
    private static MediaSortOrder ParseSort(string? value) => value?.ToLowerInvariant() switch
    {
        "name" or "title" => MediaSortOrder.TitleAsc,
        "name-desc" or "title-desc" or "za" => MediaSortOrder.TitleDesc,
        "added" or "recent" or "recentlyadded" or "date" => MediaSortOrder.AddedDesc,
        "added-asc" or "oldest" => MediaSortOrder.AddedAsc,
        "year" => MediaSortOrder.YearDesc,
        "duration" => MediaSortOrder.DurationDesc,
        "size" => MediaSortOrder.SizeDesc,
        "played" or "lastplayed" => MediaSortOrder.LastPlayedDesc,
        _ => MediaSortOrder.TitleAsc,
    };

    private static StreamQuality ParseQuality(string? value) => value?.ToLowerInvariant() switch
    {
        "original" => StreamQuality.Original,
        "high" => StreamQuality.High,
        "medium" => StreamQuality.Medium,
        "low" => StreamQuality.Low,
        _ => StreamQuality.Auto,
    };

    private static double? ParseSeconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : null;

    /// <summary>Frame position inside the file (0..1, default 10%) used for a fresh thumbnail.</summary>
    private static double ParsePercent(HttpContext http) =>
        double.TryParse(
            http.Request.Query["at"].ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? Math.Clamp(value, 0, 1)
            : 0.1;

    private static ThumbnailSize ParseThumbnailSize(string? value) => value?.ToLowerInvariant() switch
    {
        "detail" or "960" => ThumbnailSize.Detail,
        "wide" or "1280" or "backdrop" => ThumbnailSize.Wide,
        _ => ThumbnailSize.Card,
    };

    private static int? Positive(int value) => value > 0 ? value : null;

    /// <summary>The file the client asked for, or the item's primary file when none was named.</summary>
    private static MediaFileRecord? ResolveFile(MediaItem item, long? fileId) => fileId is > 0
        ? item.Files.FirstOrDefault(file => file.Id == fileId) ?? item.PrimaryFile
        : item.PrimaryFile;
}

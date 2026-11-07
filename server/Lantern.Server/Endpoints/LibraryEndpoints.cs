using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Media.Scanning;
using Lantern.Server.Security;
using Lantern.Server.Services;

namespace Lantern.Server.Endpoints;

/// <summary>Library overview, folder management, scans and the show list.</summary>
public static class LibraryEndpoints
{
    private const string FolderRoute = ApiRoutes.LibraryFolders + "/{folderId:long}";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Libraries, OverviewAsync).RequireLanternAuth();
        app.MapGet(ApiRoutes.LibraryAlias, OverviewAsync).RequireLanternAuth();
        app.MapGet(ApiRoutes.LibraryFolders, FoldersAsync).RequireLanternAuth();
        app.MapPost(ApiRoutes.LibraryFolders, AddFolderAsync).RequireLanternAdmin();
        app.MapDelete(FolderRoute, DeleteFolderAsync).RequireLanternAdmin();
        app.MapPost(ApiRoutes.LibraryScan, ScanAsync).RequireLanternAdmin();
        app.MapGet(ApiRoutes.LibraryShows, ShowsAsync).RequireLanternAuth();
    }

    /// <summary>GET /api/v1/libraries - what the home screen of the phone needs.</summary>
    private static async Task<IResult> OverviewAsync(
        HttpContext http,
        MediaRepository media,
        LibraryFolderRepository folders,
        LibraryStatsService stats,
        ScanCoordinator scans,
        ProgressService progress,
        CancellationToken ct)
    {
        var counts = await media.GetCountsAsync(ct).ConfigureAwait(false);
        var configured = await folders.GetAllAsync(false, ct).ConfigureAwait(false);
        var totals = await stats.GetFolderTotalsAsync(ct).ConfigureAwait(false);

        var page = await media.GetPageAsync(
            new MediaQuery { IncludeFiles = true, Limit = 20, Sort = MediaSortOrder.AddedDesc }, ct)
            .ConfigureAwait(false);

        var folderDtos = configured.Select(folder =>
        {
            var folderTotals = totals.TryGetValue(folder.Id, out var value) ? value : default;
            return DtoMapper.Folder(folder, folderTotals.Items, folderTotals.Bytes);
        }).ToList();

        var shelf = new List<MediaSummaryDto>();
        foreach (var resume in await progress.ContinueAsync(http.Auth().DeviceId, 20, ct).ConfigureAwait(false))
        {
            var item = await media.GetItemAsync(resume.MediaId, includeFiles: true, ct).ConfigureAwait(false);
            if (item is null) continue;

            var summary = DtoMapper.Summary(item);
            summary.PositionMs = resume.PositionMs;
            summary.DurationMs = resume.DurationMs > 0 ? resume.DurationMs : summary.DurationMs;
            summary.ProgressPercent = resume.Percent;
            summary.Completed = resume.Completed;
            shelf.Add(summary);
        }

        return ApiResults.Ok(new LibraryDto
        {
            Counts = counts,
            Folders = folderDtos,
            Scanning = scans.Status,
            ContinueWatching = shelf,
            RecentlyAdded = page.Items.Select(i => DtoMapper.Summary(i, i.PrimaryFile)).ToList(),
            LastScanUtc = counts.LastScanUtc,
        });
    }

    private static async Task<IResult> FoldersAsync(
        LibraryFolderRepository folders, LibraryStatsService stats, CancellationToken ct)
    {
        var configured = await folders.GetAllAsync(false, ct).ConfigureAwait(false);
        var totals = await stats.GetFolderTotalsAsync(ct).ConfigureAwait(false);

        var list = configured.Select(folder =>
        {
            var folderTotals = totals.TryGetValue(folder.Id, out var value) ? value : default;
            return DtoMapper.Folder(folder, folderTotals.Items, folderTotals.Bytes);
        }).ToList();

        return ApiResults.Ok(list);
    }

    /// <summary>POST /api/v1/libraries/folders - register a folder (and scan it).</summary>
    private static async Task<IResult> AddFolderAsync(
        HttpContext http,
        LibraryFolderRepository folders,
        ScanCoordinator scans,
        CancellationToken ct)
    {
        var request = await ApiResults.BodyAsync<AddFolderRequestDto>(http, ct).ConfigureAwait(false);
        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return ApiResults.BadRequest("path is required.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.Path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ApiResults.BadRequest("That folder path is not valid.", "invalid_path", ex.Message);
        }

        if (!Directory.Exists(full))
        {
            return ApiResults.NotFound($"Folder not found on this machine: {full}", "path_not_found");
        }

        var folder = new LibraryFolder
        {
            Path = full,
            DisplayName = request.DisplayName,
            Recursive = request.Recursive,
            WatchForChanges = request.WatchForChanges,
            Type = request.Type,
            Enabled = true,
            AddedUtc = DateTime.UtcNow,
        };

        await folders.AddAsync(folder, ct).ConfigureAwait(false);

        if (request.ScanNow) scans.Start(new ScanOptions { FolderIds = [folder.Id] });

        return ApiResults.Created(DtoMapper.Folder(folder, folder.LastScanFileCount, 0));
    }

    /// <summary>DELETE /api/v1/libraries/folders/{folderId} - forget a folder and its media.</summary>
    private static async Task<IResult> DeleteFolderAsync(
        long folderId, LibraryFolderRepository folders, CancellationToken ct)
    {
        var folder = await folders.GetAsync(folderId, ct).ConfigureAwait(false);
        if (folder is null) return ApiResults.NotFound($"Library folder {folderId} is not registered.");

        await folders.DeleteAsync(folderId, ct).ConfigureAwait(false);
        return ApiResults.NoContent();
    }

    /// <summary>
    /// POST /api/v1/libraries/scan - starts a scan by default (202 + live
    /// <see cref="ScanStatusDto"/>); <c>?wait=true</c> blocks and returns the report.
    /// </summary>
    private static async Task<IResult> ScanAsync(
        HttpContext http, ScanCoordinator scans, CancellationToken ct)
    {
        var request = await ApiResults.BodyAsync<ScanRequestDto>(http, ct).ConfigureAwait(false)
            ?? new ScanRequestDto();

        var options = new ScanOptions { Force = request.Force, FolderIds = request.FolderIds };

        if (!(ApiResults.QueryBool(http, "wait") ?? false))
        {
            scans.Start(options);
            return ApiResults.Accepted(scans.Status);
        }

        try
        {
            var report = await scans.RunAsync(options, ct).ConfigureAwait(false);
            return ApiResults.Ok(report);
        }
        catch (InvalidOperationException ex)
        {
            return ApiResults.Conflict(ex.Message, "scan_running");
        }
    }

    /// <summary>GET /api/v1/libraries/shows - one entry per show for the TV browser.</summary>
    private static async Task<IResult> ShowsAsync(MediaRepository media, CancellationToken ct)
    {
        var shows = await media.GetShowsAsync(ct).ConfigureAwait(false);
        foreach (var show in shows)
        {
            show.ThumbnailUrl = show.Latest is null ? null : ApiRoutes.MediaThumbnail(show.Latest.Id);
        }

        return ApiResults.Ok(shows);
    }
}

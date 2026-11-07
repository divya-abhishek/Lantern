using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;

namespace Lantern.Desktop.Services;

/// <summary>A pending pairing request as the PC sees it (the projector shows the id as a PIN).</summary>
public sealed record PendingPairing(
    string RequestId,
    string DeviceId,
    string Name,
    string? Model,
    string? Platform,
    string? IpAddress,
    DateTime CreatedUtc,
    int ExpiresInSeconds);

/// <summary>The subset of the scan report the UI reports back to the user.</summary>
public sealed record ScanSummary(int FilesSeen, int ItemsAdded, int ItemsUpdated, int ItemsRemoved, int Errors);

/// <summary>
/// Typed client for the Lantern server API. Every route comes from <see cref="ApiRoutes"/> and
/// every payload is (de)serialized with <see cref="ApiJson.Options"/>, so the desktop app cannot
/// drift from the Android client or the server.
/// </summary>
public sealed class LanternClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private ServerInfoDto? _info;

    /// <summary>Root of the attached server, e.g. <c>http://192.168.1.50:8787</c>.</summary>
    public string? ServerUrl => _http.BaseAddress?.GetLeftPart(UriPartial.Authority);

    public ServerInfoDto? Info => _info;

    public bool IsAttached => _http.BaseAddress is not null;

    /// <summary>Points the client at a server. Requests from this machine count as local admin.</summary>
    public void Attach(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) throw new ArgumentException("serverUrl is required", nameof(serverUrl));

        var url = serverUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;

        _http.BaseAddress = new Uri(url + "/", UriKind.Absolute);
        _http.DefaultRequestHeaders.Remove("X-Lantern-Device");
        _http.DefaultRequestHeaders.Add("X-Lantern-Device", "lantern-desktop");
        _info = null;
    }

    public void Detach()
    {
        _http.BaseAddress = null;
        _info = null;
    }

    /// <summary>GET /server/info - also the "is this really a Lantern server?" probe.</summary>
    public async Task<ServerInfoDto> GetInfoAsync(CancellationToken ct = default)
    {
        var info = await GetAsync<ServerInfoDto>(ApiRoutes.ServerInfo, ct).ConfigureAwait(false);
        _info = info;
        return info;
    }

    public Task<LibraryDto> GetLibraryAsync(CancellationToken ct = default) =>
        GetAsync<LibraryDto>(ApiRoutes.Libraries, ct);

    /// <summary>GET /media - the library grid, filtered and paged.</summary>
    public async Task<List<MediaSummaryDto>> ListMediaAsync(
        string? query = null,
        string? kind = null,
        string? sort = null,
        long? folderId = null,
        int limit = 60,
        int offset = 0,
        CancellationToken ct = default)
    {
        var url = $"{ApiRoutes.Media}?limit={limit}&offset={offset}" +
                  $"&kind={Uri.EscapeDataString(kind ?? "all")}" +
                  $"&sort={Uri.EscapeDataString(sort ?? "added")}";

        if (!string.IsNullOrWhiteSpace(query)) url += $"&query={Uri.EscapeDataString(query.Trim())}";
        if (folderId is > 0) url += $"&folderId={folderId}";

        var page = await GetAsync<MediaPageResponse>(url, ct).ConfigureAwait(false);
        return page.Items;
    }

    /// <summary>GET /media/{id} - optionally including the playback plan for this PC.</summary>
    public Task<MediaDetailDto?> GetMediaAsync(long mediaId, bool plan = false, CancellationToken ct = default) =>
        GetOrNullAsync<MediaDetailDto>(
            plan ? $"{ApiRoutes.MediaItem(mediaId)}?plan=true" : ApiRoutes.MediaItem(mediaId), ct);

    public Task<List<SubtitleDto>> GetSubtitlesAsync(long mediaId, CancellationToken ct = default) =>
        GetAsync<List<SubtitleDto>>(ApiRoutes.MediaSubtitles(mediaId), ct);

    /// <summary>POST /playback/plan - how should this file play on the target device?</summary>
    public Task<PlaybackPlanDto> PlanAsync(
        long mediaId, StreamQuality quality = StreamQuality.Auto, CancellationToken ct = default) =>
        PostAsync<PlaybackPlanRequestDto, PlaybackPlanDto>(
            ApiRoutes.PlaybackPlan,
            new PlaybackPlanRequestDto { MediaId = mediaId, Quality = quality },
            ct);

    /// <summary>Reports where the PC player is, so resume works across devices.</summary>
    public Task<ProgressAckDto> ReportProgressAsync(
        long mediaId, long positionMs, long durationMs, bool playing, CancellationToken ct = default) =>
        PostAsync<ProgressReportDto, ProgressAckDto>(
            ApiRoutes.PlaybackProgress(mediaId),
            new ProgressReportDto
            {
                MediaId = mediaId,
                PositionMs = positionMs,
                DurationMs = durationMs,
                State = playing ? PlaybackState.Playing : PlaybackState.Paused,
            },
            ct);

    /// <summary>Absolute URL a player should open for an item (direct stream or HLS playlist).</summary>
    public async Task<string?> ResolvePlayUrlAsync(long mediaId, CancellationToken ct = default)
    {
        var plan = await PlanAsync(mediaId, StreamQuality.Auto, ct).ConfigureAwait(false);
        if (plan.PlayUrl is null || _http.BaseAddress is null) return null;

        return new Uri(_http.BaseAddress, plan.PlayUrl.TrimStart('/')).ToString();
    }

    // --- devices & pairing -------------------------------------------------------------

    public Task<List<DiscoveryReceiverDto>> GetReceiversAsync(CancellationToken ct = default) =>
        GetAsync<List<DiscoveryReceiverDto>>(ApiRoutes.Devices + "/receivers", ct);

    public Task<List<TrustedDeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        GetAsync<List<TrustedDeviceDto>>(ApiRoutes.Devices, ct);

    public Task RevokeDeviceAsync(long deviceRowId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.DeviceRevoke(deviceRowId), null, ct);

    /// <summary>Unpaired devices that are waiting for the PIN shown on the projector.</summary>
    public Task<List<PendingPairing>> GetPairingRequestsAsync(CancellationToken ct = default) =>
        GetAsync<List<PendingPairing>>(ApiRoutes.PairingRequests, ct);

    public Task InviteReceiverAsync(string deviceId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.Devices + "/receivers/" + Uri.EscapeDataString(deviceId) + "/connect", null, ct);

    /// <summary>Approves the request whose id equals the PIN typed on the PC.</summary>
    public Task ApprovePairingAsync(string requestId, bool trust, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.PairingApprove(requestId), new { trust }, ct);

    public Task RejectPairingAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.PairingReject(requestId), new { reason = "Rejected on the PC." }, ct);

    // --- library folders & scanning -----------------------------------------------------

    public Task<List<FolderDto>> GetFoldersAsync(CancellationToken ct = default) =>
        GetAsync<List<FolderDto>>(ApiRoutes.LibraryFolders, ct);

    public Task<FolderDto> AddFolderAsync(string path, bool scanNow, CancellationToken ct = default) =>
        PostAsync<object, FolderDto>(ApiRoutes.LibraryFolders, new { path, scanNow }, ct);

    public Task DeleteFolderAsync(long folderId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, ApiRoutes.LibraryFolder(folderId), null, ct);

    /// <summary>Runs a scan and waits for its report (?wait=true).</summary>
    public async Task<ScanSummary> ScanAsync(long? folderId, CancellationToken ct = default)
    {
        var body = new
        {
            force = true,
            folderIds = folderId is > 0 ? new[] { folderId.Value } : null,
        };

        var report = await PostAsync<object, ScanReportResponse>(ApiRoutes.LibraryScan + "?wait=true", body, ct)
            .ConfigureAwait(false);

        return new ScanSummary(
            report.FilesSeen, report.ItemsAdded, report.FilesUpdated, report.ItemsRemoved, report.Errors);
    }

    /// <summary>Loads a poster frame as a frozen bitmap (safe to hand to any thread).</summary>
    public async Task<ImageSource?> GetThumbnailAsync(long mediaId, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;

        try
        {
            using var response = await _http.GetAsync(ApiRoutes.MediaThumbnail(mediaId), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A missing poster is not an error the user needs to see: the grid shows a placeholder.
            return null;
        }
    }

    // --- screen mirroring ---------------------------------------------------------------

    /// <summary>
    /// GET /mirror/capabilities - what this PC can capture. The server verifies each encoder with a
    /// real test encode, so this call takes a few seconds the first time and is then cached.
    /// </summary>
    public Task<MirrorCapabilitiesDto> GetMirrorCapabilitiesAsync(
        bool refresh = false, CancellationToken ct = default) =>
        GetAsync<MirrorCapabilitiesDto>(
            ApiRoutes.MirrorCapabilities + (refresh ? "?refresh=true" : string.Empty), ct);

    /// <summary>GET /mirror/status - the live numbers the Screen Mirror page shows.</summary>
    public Task<MirrorStatusDto> GetMirrorStatusAsync(CancellationToken ct = default) =>
        GetAsync<MirrorStatusDto>(ApiRoutes.MirrorStatus, ct);

    /// <summary>
    /// POST /mirror/start. A refusal comes back as JSON rather than an exception, because the reason
    /// ("no display", "no encoder") is exactly what the user needs to read.
    /// </summary>
    public async Task<MirrorStatusDto> StartMirrorAsync(
        MirrorSettingsDto settings, CancellationToken ct = default)
    {
        using var response = await _http
            .PostAsJsonAsync(ApiRoutes.MirrorStart, settings, ApiJson.Options, ct)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return await ReadAsync<MirrorStatusDto>(response, ct).ConfigureAwait(false);

        var error = await response.Content
            .ReadFromJsonAsync<ApiErrorDto>(ApiJson.Options, ct)
            .ConfigureAwait(false);

        throw new InvalidOperationException(
            error?.Message ?? $"The PC refused to start mirroring (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>POST /mirror/stop - releases the capture, the encoder and the audio device.</summary>
    public async Task<MirrorStopDto> StopMirrorAsync(CancellationToken ct = default)
    {
        using var response = await _http
            .PostAsJsonAsync(ApiRoutes.MirrorStop, new { }, ApiJson.Options, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await ReadAsync<MirrorStopDto>(response, ct).ConfigureAwait(false);
    }

    // --- plumbing ----------------------------------------------------------------------

    private sealed record MediaPageResponse(List<MediaSummaryDto> Items, int Total, int Limit, int Offset);

    private sealed record ScanReportResponse(
        int FilesSeen, int FilesAdded, int FilesUpdated, int FilesRemoved,
        int ItemsAdded, int ItemsRemoved, int Errors);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return default;

        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(url, body, ApiJson.Options, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<TResponse>(response, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: ApiJson.Options);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException("The server returned an empty response.");
    }

    public void Dispose() => _http.Dispose();
}

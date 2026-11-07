namespace Lantern.Core.Api;

/// <summary>
/// Single source of truth for every HTTP route. The server, the WPF app and the
/// Android client all reference these so a path never drifts between platforms.
/// </summary>
public static class ApiRoutes
{
    public const string Prefix = "/api/v1";

    // --- discovery ---------------------------------------------------------
    public const string ServerInfo = Prefix + "/server/info";
    public const string ServerSettings = Prefix + "/server/settings";
    public const string AuthCheck = Prefix + "/auth/check";
    public const string SpeedTestPayload = Prefix + "/speedtest/payload";
    public const string Diagnostics = Prefix + "/diagnostics";

    // --- library -----------------------------------------------------------
    public const string Libraries = Prefix + "/libraries";

    /// <summary>Singular alias of <see cref="Libraries"/> kept for older drafts of the DTO docs.</summary>
    public const string LibraryAlias = Prefix + "/library";

    /// <summary>GET (list) / POST (add a folder).</summary>
    public const string LibraryFolders = Libraries + "/folders";

    /// <summary>DELETE one configured library folder.</summary>
    public static string LibraryFolder(long folderId) => $"{LibraryFolders}/{folderId}";

    /// <summary>POST: start a background rescan of one, several or every folder.</summary>
    public const string LibraryScan = Libraries + "/scan";

    /// <summary>GET: distinct show titles with episode counts.</summary>
    public const string LibraryShows = Libraries + "/shows";

    /// <summary>GET with ?query=&amp;kind=&amp;folderId=&amp;limit=&amp;offset=&amp;sort=</summary>
    public const string Media = Prefix + "/media";

    public static string MediaItem(long mediaId) => $"{Media}/{mediaId}";

    /// <summary>Direct-play byte stream (supports HTTP Range).</summary>
    public static string MediaStream(long mediaId) => $"{Media}/{mediaId}/stream";

    /// <summary>Lists external + embedded subtitle tracks.</summary>
    public static string MediaSubtitles(long mediaId) => $"{Media}/{mediaId}/subtitles";

    /// <summary>WebVTT conversion of a single subtitle track (Media3 friendly).</summary>
    public static string MediaSubtitleVtt(long mediaId, string subtitleId) =>
        $"{Media}/{mediaId}/subtitles/{Uri.EscapeDataString(subtitleId)}.vtt";

    public static string MediaThumbnail(long mediaId) => $"{Media}/{mediaId}/thumbnail.jpg";

    /// <summary>Forced refresh of the ffprobe metadata for one item.</summary>
    public static string MediaProbe(long mediaId) => $"{Media}/{mediaId}/probe";

    // --- playback ----------------------------------------------------------
    /// <summary>POST: pick DirectPlay / Remux / Transcode for a device.</summary>
    public const string PlaybackPlan = Prefix + "/playback/plan";

    public static string PlaybackProgress(long mediaId) => $"{Prefix}/playback/{mediaId}/progress";

    public const string PlaybackContinue = Prefix + "/playback/continue";

    /// <summary>HLS master playlist of a running transcode session.</summary>
    public static string TranscodeMaster(long mediaId) => $"{Media}/{mediaId}/transcode/master.m3u8";

    public static string TranscodePlaylist(long mediaId, string sessionId) =>
        $"{Media}/{mediaId}/transcode/{Uri.EscapeDataString(sessionId)}/playlist.m3u8";

    public static string TranscodeSegment(long mediaId, string sessionId, string segment) =>
        $"{TranscodePlaylist(mediaId, sessionId)}/{Uri.EscapeDataString(segment)}";

    public static string TranscodeStop(long mediaId) => $"{Media}/{mediaId}/transcode/stop";

    // --- screen mirror -----------------------------------------------------
    /// <summary>GET: displays, capture backends, verified H.264 encoders and audio support.</summary>
    public const string MirrorCapabilities = Prefix + "/mirror/capabilities";

    /// <summary>POST: start capturing this PC's screen and streaming it to the projector.</summary>
    public const string MirrorStart = Prefix + "/mirror/start";

    /// <summary>POST: stop the capture session.</summary>
    public const string MirrorStop = Prefix + "/mirror/stop";

    /// <summary>GET: the live state, including the RTSP URL receivers open.</summary>
    public const string MirrorStatus = Prefix + "/mirror/status";

    // --- pairing / devices -------------------------------------------------
    public const string PairingRequest = Prefix + "/pairing/request";
    public const string PairingRequests = Prefix + "/pairing/requests";

    public static string PairingStatus(string requestId) =>
        $"{Prefix}/pairing/status/{Uri.EscapeDataString(requestId)}";

    public static string PairingApprove(string requestId) =>
        $"{Prefix}/pairing/approve/{Uri.EscapeDataString(requestId)}";

    public static string PairingReject(string requestId) =>
        $"{Prefix}/pairing/reject/{Uri.EscapeDataString(requestId)}";

    public const string Devices = Prefix + "/devices";

    /// <summary>POST: the client reports what it can decode so the planner can trust it.</summary>
    public const string DeviceCapabilities = Devices + "/capabilities";

    public static string Device(long id) => $"{Devices}/{id}";

    public static string DeviceRevoke(long id) => $"{Devices}/{id}/revoke";

    /// <summary>Signed, short lived URL the Android client uses for image loading.</summary>
    public const string WebSocket = Prefix + "/ws";

    /// <summary>Header carrying the pairing token.</summary>
    public const string TokenHeader = "X-Lantern-Token";

    public const string DeviceIdHeader = "X-Lantern-Device";
}

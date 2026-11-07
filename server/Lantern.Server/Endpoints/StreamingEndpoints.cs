using System.Globalization;
using System.Text;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Media.Transcoding;
using Lantern.Server.Security;
using Lantern.Server.Services;

namespace Lantern.Server.Endpoints;
public static class StreamingEndpoints
{
    private const int CopyBufferBytes = 256 * 1024;

    private const string StreamRoute = ApiRoutes.Media + "/{mediaId:long}/stream";
    private const string PreparedRoute = ApiRoutes.Media + "/{mediaId:long}/transcode/{sessionId}/media.mp4";
    private const string StopRoute = ApiRoutes.Media + "/{mediaId:long}/transcode/stop";
    public sealed record TranscodeStopDto(int Stopped, string? SessionId, string? ClientId);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapMethods(StreamRoute, [HttpMethods.Get, HttpMethods.Head], StreamAsync).RequireLanternAuth();
        app.MapMethods(PreparedRoute, [HttpMethods.Get, HttpMethods.Head], PreparedAsync).RequireLanternAuth();
        app.MapPost(StopRoute, StopAsync).RequireLanternAuth();
    }
    private static async Task<IResult> StreamAsync(
        long mediaId,
        HttpContext http,
        MediaRepository media,
        StreamRegistry streams,
        CancellationToken ct)
    {
        var item = await media.GetItemAsync(mediaId, includeFiles: true, ct).ConfigureAwait(false);
        if (item is null) return ApiResults.NotFound($"Media {mediaId} was not found.");

        var file = ResolveFile(item, ApiResults.QueryLong(http, "fileId"));
        if (file is null) return ApiResults.NotFound($"Media {mediaId} has no playable file.");

        FileStream handle;
        try
        {
            handle = new FileStream(
                file.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ApiResults.NotFound(
                $"\"{file.FileName}\" is no longer on disk. Rescan the library.", "file_missing");
        }
        catch (IOException ex)
        {
            return ApiResults.Unavailable(
                $"\"{file.FileName}\" could not be opened.", "file_locked", ex.Message);
        }

        await using (handle.ConfigureAwait(false))
        {
            var length = handle.Length;
            http.Response.Headers.AcceptRanges = "bytes";
            http.Response.ContentType = ContentTypeFor(file);
            http.Response.Headers["X-Lantern-File"] = file.FileName;
            http.Response.Headers.CacheControl = "no-store";

            if (length <= 0)
            {
                http.Response.Headers.ContentLength = 0;
                return Results.Empty;
            }

            var range = ParseRange(http.Request.Headers.Range.ToString(), length);
            if (range is null)
            {
                http.Response.Headers.ContentRange = $"bytes */{length}";
                http.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return Results.Empty;
            }

            var (start, end) = range.Value;
            var count = end - start + 1;

            http.Response.Headers.ContentLength = count;
            var requestedRange = http.Request.Headers.Range.ToString();
            if (requestedRange.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && !requestedRange.Contains(','))
            {
                http.Response.StatusCode = StatusCodes.Status206PartialContent;
                http.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
            }
            if (HttpMethods.IsHead(http.Request.Method)) return Results.Empty;

            var auth = http.Auth();
            var telemetry = streams.Begin(
                item.Id,
                string.IsNullOrWhiteSpace(item.Title) ? file.FileName : item.Title,
                string.IsNullOrWhiteSpace(auth.DeviceId) ? "anonymous" : auth.DeviceId,
                directPlay: true,
                http.Connection.RemoteIpAddress?.ToString());
            telemetry.CountRangeRequest();

            handle.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[CopyBufferBytes];
            var remaining = count;

            try
            {
                while (remaining > 0)
                {
                    var wanted = (int)Math.Min(buffer.Length, remaining);
                    var read = await handle.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    telemetry.AddBytes(read);
                    remaining -= read;
                }
            }
            catch (OperationCanceledException)
            {
            }

            return Results.Empty;
        }
    }
    private static IResult PreparedAsync(long mediaId, string sessionId, HttpContext http, TranscodeSessionManager transcodes)
    {
        var session = transcodes.Get(sessionId);
        if (session is null || session.MediaId != mediaId) return ApiResults.NotFound("Preparation expired. Open the title again.");
        if (!http.Auth().IsAdmin && session.ClientId != http.Auth().DeviceId) return ApiResults.Forbidden("This preparation belongs to another device.");
        session.Touch();
        if (!session.Ready) return ApiResults.Unavailable("The PC is still preparing this file.", "preparing");
        // Holding a read handle prevents cache pruning during a response.
        var stream = new FileStream(session.CompletedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Results.File(stream, "video/mp4", enableRangeProcessing: true);
    }

    private static async Task<IResult> StopAsync(
        long mediaId,
        HttpContext http,
        TranscodeSessionManager transcodes)
    {
        var auth = http.Auth();
        var sessionId = ApiResults.QueryText(http, "sessionId");

        if (sessionId is not null)
        {
            var session = transcodes.Get(sessionId);
            if (session is null || (session.MediaId > 0 && session.MediaId != mediaId))
            {
                return ApiResults.NotFound(
                    $"Transcode session \"{sessionId}\" is not running.", "session_not_found");
            }

            if (!auth.IsAdmin
                && !string.Equals(session.ClientId, auth.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.Forbidden("That transcode session belongs to another device.");
            }

            await transcodes.StopAsync(sessionId, "stopped by client").ConfigureAwait(false);
            return ApiResults.Ok(new TranscodeStopDto(1, sessionId, session.ClientId));
        }

        if (!string.IsNullOrWhiteSpace(auth.DeviceId))
        {
            var stopped = await transcodes
                .StopAllForClientAsync(auth.DeviceId, "stopped by client")
                .ConfigureAwait(false);

            return ApiResults.Ok(new TranscodeStopDto(stopped, null, auth.DeviceId));
        }

        if (!auth.IsAdmin)
        {
            return ApiResults.Forbidden("Pass ?sessionId= to stop a specific transcode session.");
        }

        var running = transcodes.Sessions.Count(session => session.IsRunning);
        await transcodes.StopAllAsync("stopped on the PC").ConfigureAwait(false);
        return ApiResults.Ok(new TranscodeStopDto(running, null, null));
    }
    private static MediaFileRecord? ResolveFile(MediaItem item, long? fileId) => fileId is > 0
        ? item.Files.FirstOrDefault(file => file.Id == fileId) ?? item.PrimaryFile
        : item.PrimaryFile;
    private static string ContentTypeFor(MediaFileRecord file)
    {
        var container = string.IsNullOrWhiteSpace(file.Container)
            ? Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant()
            : file.Container!.ToLowerInvariant();

        return container switch
        {
            "mp4" or "m4v" or "mov" => "video/mp4",
            "webm" => "video/webm",
            "mkv" or "matroska" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "ts" or "m2ts" or "mts" => "video/mp2t",
            "mpg" or "mpeg" or "vob" => "video/mpeg",
            "flv" => "video/x-flv",
            "wmv" or "asf" => "video/x-ms-wmv",
            "mp3" => "audio/mpeg",
            "m4a" or "aac" => "audio/mp4",
            "flac" => "audio/flac",
            "ogg" or "oga" => "audio/ogg",
            _ => "application/octet-stream",
        };
    }

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
    private static (long Start, long End)? ParseRange(string? header, long length)
    {
        var whole = (0L, Math.Max(0, length - 1));

        var value = header?.Trim();
        if (string.IsNullOrEmpty(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return whole;
        }

        value = value[6..].Trim();
        if (value.Contains(',')) return whole;

        var dash = value.IndexOf('-');
        if (dash < 0) return null;

        var startText = value[..dash].Trim();
        var endText = value[(dash + 1)..].Trim();
        var suffixRequest = startText.Length == 0;

        long start;
        if (suffixRequest)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0) return null;
            start = Math.Max(0, length - suffix);
        }
        else
        {
            if (!long.TryParse(startText, out start) || start < 0) return null;
            if (start >= length) return null;
        }

        var end = length - 1;
        if (!suffixRequest && endText.Length > 0)
        {
            if (!long.TryParse(endText, out var parsedEnd)) return null;
            end = Math.Min(parsedEnd, length - 1);
        }

        return end < start ? null : (start, end);
    }
}

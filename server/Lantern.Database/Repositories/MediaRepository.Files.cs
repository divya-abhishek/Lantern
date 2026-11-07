using System.Text.Json;
using Lantern.Core.Api;
using Lantern.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lantern.Database.Repositories;

/// <summary>File-level persistence (one row per playable file on disk).</summary>
public sealed partial class MediaRepository
{
    internal const string FileColumns = """
        id, media_id, path, container, size_bytes, duration_ms, video_codec, audio_codec,
        width, height, frame_rate, bitrate_bps, pixel_format, is_hdr, dv_profile,
        audio_stream_count, audio_tracks_json, subtitle_tracks_json, last_write_utc, last_scanned_utc
        """;

    private static readonly JsonSerializerOptions TrackJson = ApiJson.Options;

    internal static MediaFileRecord MapFile(SqliteDataReader r)
    {
        var file = new MediaFileRecord
        {
            Id = r.GetInt64("id"),
            MediaId = r.GetInt64("media_id"),
            Path = r.GetStringOrNull("path") ?? string.Empty,
            Container = r.GetStringOrNull("container"),
            SizeBytes = r.GetInt64("size_bytes"),
            DurationMs = r.GetInt64("duration_ms"),
            VideoCodec = r.GetStringOrNull("video_codec"),
            AudioCodec = r.GetStringOrNull("audio_codec"),
            Width = r.GetInt32OrNull("width"),
            Height = r.GetInt32OrNull("height"),
            FrameRate = r.GetDoubleOrNull("frame_rate"),
            BitrateBps = r.GetInt64OrNull("bitrate_bps"),
            PixelFormat = r.GetStringOrNull("pixel_format"),
            IsHdr = r.GetBool("is_hdr"),
            DolbyVisionProfile = r.GetStringOrNull("dv_profile"),
            AudioStreamCount = r.GetInt32("audio_stream_count"),
            LastWriteUtc = r.GetDateTime("last_write_utc"),
            LastScannedUtc = r.GetDateTime("last_scanned_utc"),
        };

        file.AudioTracks = Deserialize<List<MediaTrackInfo>>(r.GetStringOrNull("audio_tracks_json")) ?? [];
        file.SubtitleTracks = Deserialize<List<SubtitleTrackInfo>>(r.GetStringOrNull("subtitle_tracks_json")) ?? [];
        if (file.AudioStreamCount == 0 && file.AudioTracks.Count > 0)
        {
            file.AudioStreamCount = file.AudioTracks.Count;
        }
        return file;
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, TrackJson);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal async Task<List<MediaFileRecord>> GetFilesAsync(
        SqliteConnection connection, long mediaId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {FileColumns} FROM media_files WHERE media_id = $media ORDER BY size_bytes DESC";
        cmd.AddParam("$media", mediaId);

        var files = new List<MediaFileRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            files.Add(MapFile(reader));
        }
        return files;
    }

    public async Task<List<MediaFileRecord>> GetFilesAsync(long mediaId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await GetFilesAsync(connection, mediaId, ct).ConfigureAwait(false);
    }

    public async Task<MediaFileRecord?> GetFileAsync(long fileId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {FileColumns} FROM media_files WHERE id = $id";
        cmd.AddParam("$id", fileId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapFile(reader) : null;
    }

    public async Task<MediaFileRecord?> GetFileByPathAsync(string path, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {FileColumns} FROM media_files WHERE path = $path COLLATE NOCASE LIMIT 1";
        cmd.AddParam("$path", Path.GetFullPath(path));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapFile(reader) : null;
    }

    /// <summary>Inserts a file row (or updates it when the path already exists) and returns its id.</summary>
    public async Task<long> UpsertFileAsync(MediaFileRecord file, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(file.Path);
        var now = DateTime.UtcNow;
        var lastWrite = file.LastWriteUtc == default ? now : file.LastWriteUtc;
        var lastScan = file.LastScannedUtc == default ? now : file.LastScannedUtc;

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO media_files
                (media_id, path, container, size_bytes, duration_ms, video_codec, audio_codec,
                 width, height, frame_rate, bitrate_bps, pixel_format, is_hdr, dv_profile,
                 audio_stream_count, audio_tracks_json, subtitle_tracks_json, last_write_utc, last_scanned_utc)
            VALUES
                ($media, $path, $container, $size, $duration, $video, $audio, $width, $height,
                 $fps, $bitrate, $pixel, $hdr, $dv, $audioCount, $audioJson, $subsJson, $lastWrite, $lastScan)
            ON CONFLICT (path) DO UPDATE SET
                media_id = excluded.media_id,
                container = excluded.container,
                size_bytes = excluded.size_bytes,
                duration_ms = excluded.duration_ms,
                video_codec = excluded.video_codec,
                audio_codec = excluded.audio_codec,
                width = excluded.width,
                height = excluded.height,
                frame_rate = excluded.frame_rate,
                bitrate_bps = excluded.bitrate_bps,
                pixel_format = excluded.pixel_format,
                is_hdr = excluded.is_hdr,
                dv_profile = excluded.dv_profile,
                audio_stream_count = excluded.audio_stream_count,
                audio_tracks_json = excluded.audio_tracks_json,
                subtitle_tracks_json = excluded.subtitle_tracks_json,
                last_write_utc = excluded.last_write_utc,
                last_scanned_utc = excluded.last_scanned_utc
            RETURNING id;
            """;
        cmd.AddParam("$media", file.MediaId)
            .AddParam("$path", path)
            .AddParam("$container", file.Container)
            .AddParam("$size", file.SizeBytes)
            .AddParam("$duration", file.DurationMs)
            .AddParam("$video", file.VideoCodec)
            .AddParam("$audio", file.AudioCodec)
            .AddParam("$width", file.Width)
            .AddParam("$height", file.Height)
            .AddParam("$fps", file.FrameRate)
            .AddParam("$bitrate", file.BitrateBps)
            .AddParam("$pixel", file.PixelFormat)
            .AddParam("$hdr", file.IsHdr)
            .AddParam("$dv", file.DolbyVisionProfile)
            .AddParam("$audioCount", file.AudioStreamCount > 0 ? file.AudioStreamCount : file.AudioTracks.Count)
            .AddParam("$audioJson", file.AudioTracks.Count == 0 ? null : JsonSerializer.Serialize(file.AudioTracks, TrackJson))
            .AddParam("$subsJson", file.SubtitleTracks.Count == 0 ? null : JsonSerializer.Serialize(file.SubtitleTracks, TrackJson))
            .AddParam("$lastWrite", lastWrite.ToIso())
            .AddParam("$lastScan", lastScan.ToIso());

        file.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        file.Path = path;
        return file.Id;
    }

    public async Task DeleteFileAsync(long fileId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_files WHERE id = $id";
        cmd.AddParam("$id", fileId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// All file rows (optionally restricted to one folder) keyed by path. The
    /// scanner uses this to detect changed files without a probe per file.
    /// </summary>
    public async Task<Dictionary<string, MediaFileRecord>> GetFileRecordsAsync(
        long? folderId = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = folderId is null
            ? $"SELECT {FileColumns} FROM media_files"
            : $"""
                SELECT {FileColumns} FROM media_files
                WHERE media_id IN (SELECT id FROM media_items WHERE folder_id = $folder);
                """;
        if (folderId is not null)
        {
            cmd.AddParam("$folder", folderId.Value);
        }

        var map = new Dictionary<string, MediaFileRecord>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var file = MapFile(reader);
            if (!string.IsNullOrEmpty(file.Path))
            {
                map[file.Path] = file;
            }
        }
        return map;
    }
    public async Task<int> DeleteMissingFilesAsync(long mediaId, IReadOnlyCollection<string> existingPaths, CancellationToken ct = default)
    {
        var files = await GetFilesAsync(mediaId, ct).ConfigureAwait(false);
        var keep = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in files)
        {
            if (keep.Contains(file.Path)) continue;
            await DeleteFileAsync(file.Id, ct).ConfigureAwait(false);
            removed++;
        }
        return removed;
    }

    /// <summary>Removes items that no longer own a single file. Returns the deleted ids.</summary>
    public async Task<List<long>> DeleteItemsWithoutFilesAsync(long? folderId = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id FROM media_items m
            WHERE NOT EXISTS (SELECT 1 FROM media_files f WHERE f.media_id = m.id)
            """ + (folderId is null ? string.Empty : " AND m.folder_id = $folder");
        if (folderId is not null)
        {
            cmd.AddParam("$folder", folderId.Value);
        }

        var ids = new List<long>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetInt64("id"));
            }
        }

        foreach (var id in ids)
        {
            await DeleteItemAsync(id, ct).ConfigureAwait(false);
        }
        return ids;
    }
}

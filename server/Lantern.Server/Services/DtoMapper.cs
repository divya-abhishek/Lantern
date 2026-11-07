using System.Text.Json;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Tooling;
using Lantern.Media.Transcoding;

namespace Lantern.Server.Services;

/// <summary>
/// Maps Core models onto the wire DTOs documented in <c>Lantern.Core.Dtos</c>. Keeping
/// every mapping in one place means a new DTO field is a one line change here instead of
/// a hunt through the endpoint groups.
/// </summary>
internal static class DtoMapper
{
    // --- media --------------------------------------------------------------

    public static string StreamId(long mediaId, long fileId) => $"{mediaId}:{fileId}";

    public static bool TryParseStreamId(string? streamId, out long mediaId, out long fileId)
    {
        mediaId = 0;
        fileId = 0;
        if (string.IsNullOrWhiteSpace(streamId)) return false;

        var parts = streamId.Trim().Split(':', 2);
        return parts.Length == 2
            && long.TryParse(parts[0], out mediaId)
            && long.TryParse(parts[1], out fileId);
    }

    public static MediaSummaryDto Summary(
        MediaItem item, MediaFileRecord? file = null, PlaybackProgressRecord? progress = null) =>
        Base(item, file ?? item.PrimaryFile, progress);

    public static MediaDetailDto Detail(MediaItem item, PlaybackProgressRecord? progress, DeviceCapabilities? device)
    {
        var file = item.PrimaryFile;
        var detail = Base(item, file, progress);
        detail.Description = item.Description;
        detail.Files = item.Files.Select(f => FileDto(f, f.Id == item.PrimaryFileId)).ToList();
        detail.Subtitles = file is null ? [] : file.SubtitleTracks.Select(t => Subtitle(t, item.Id)).ToList();
        detail.AudioTracks = file is null ? [] : file.AudioTracks.Select(t => AudioTrack(t, device)).ToList();
        return detail;
    }

    private static MediaDetailDto Base(MediaItem item, MediaFileRecord? file, PlaybackProgressRecord? progress)
    {
        var detail = new MediaDetailDto
        {
            Id = item.Id,
            StreamId = StreamId(item.Id, file?.Id ?? item.PrimaryFileId),
            Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title,
            Year = item.Year,
            Kind = item.Kind,
            ShowTitle = item.ShowTitle,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            EpisodeCode = item.EpisodeCode,
            AddedUtc = item.AddedUtc,
            LastPlayedUtc = item.LastPlayedUtc,
            ThumbnailUrl = ApiRoutes.MediaThumbnail(item.Id),
            PositionMs = progress?.PositionMs ?? 0,
            ProgressPercent = progress?.Percent ?? 0,
            Completed = progress?.Completed ?? false,
        };

        if (file is null) return detail;

        detail.StreamUrl = ApiRoutes.MediaStream(item.Id);
        detail.FileName = file.FileName;
        detail.FolderName = FolderName(file.Path);
        detail.DurationMs = file.DurationMs;
        detail.Width = file.Width;
        detail.Height = file.Height;
        detail.VideoCodec = file.VideoCodec;
        detail.AudioCodec = file.AudioCodec;
        detail.AudioChannels = DefaultAudio(file)?.Channels;
        detail.BitrateBps = file.BitrateBps;
        detail.SizeBytes = file.SizeBytes;
        detail.Container = file.Container;
        detail.CodecLabel = file.CodecLabel;
        detail.IsHdr = file.IsHdr;
        return detail;
    }

    public static MediaFileDto FileDto(MediaFileRecord file, bool isPrimary) => new()
    {
        Id = file.Id,
        FileName = file.FileName,
        Container = string.IsNullOrWhiteSpace(file.Container) ? "unknown" : file.Container!,
        SizeBytes = file.SizeBytes,
        DurationMs = file.DurationMs,
        VideoCodec = file.VideoCodec,
        AudioCodec = file.AudioCodec,
        Width = file.Width,
        Height = file.Height,
        FrameRate = file.FrameRate,
        BitrateBps = file.BitrateBps,
        IsHdr = file.IsHdr,
        DolbyVisionProfile = file.DolbyVisionProfile,
        PixelFormat = file.PixelFormat,
        IsPrimary = isPrimary,
    };

    public static AudioTrackDto AudioTrack(MediaTrackInfo track, DeviceCapabilities? device) => new()
    {
        Index = track.Index,
        Codec = track.Codec,
        Language = track.Language,
        Title = track.Title,
        Channels = track.Channels,
        ChannelLayout = track.ChannelLayout,
        IsDefault = track.IsDefault,
        Label = track.Label,
        SupportedByDevice = device is null || device.SupportsAudio(track.Codec),
    };

    public static SubtitleDto Subtitle(SubtitleTrackInfo track, long mediaId) => new()
    {
        Id = track.Id,
        Codec = track.Codec,
        Language = track.Language,
        Title = track.Title,
        IsDefault = track.IsDefault,
        IsForced = track.IsForced,
        IsExternal = track.IsExternal,
        IsBitmap = track.IsBitmapBased,
        Label = track.Label,
        Url = ApiRoutes.MediaSubtitleVtt(mediaId, track.Id),
        MimeType = "text/vtt",
    };

    /// <summary>Serializes a plan into the client facing DTO, adding the URL to play.</summary>
    public static PlaybackPlanDto Plan(PlaybackPlan plan, long mediaId, TranscodeSession? session)
    {
        var dto = new PlaybackPlanDto
        {
            Mode = plan.Mode,
            Decision = plan.Decision,
            ModeLabel = plan.ModeLabel,
            Reason = plan.Reason,
            Notes = [.. plan.Notes],
            Container = plan.Container,
            VideoAction = plan.VideoAction,
            AudioAction = plan.AudioAction,
            OutputVideoCodec = plan.OutputVideoCodec,
            OutputAudioCodec = plan.OutputAudioCodec,
            OutputWidth = plan.OutputWidth,
            OutputHeight = plan.OutputHeight,
            OutputVideoBitrateBps = plan.OutputVideoBitrateBps,
            OutputAudioBitrateKbps = plan.OutputAudioBitrateKbps,
            AudioOnly = plan.AudioOnly,
            SessionId = session?.Id,
        };

        if (session is null)
        {
            dto.DirectStreamUrl = ApiRoutes.MediaStream(mediaId);
            dto.PlayUrl = dto.DirectStreamUrl;
            dto.MimeType = ContentTypeFor(plan.Container);
        }
        else
        {
            dto.PreparationState = session.Ready ? "Ready" : session.IsRunning ? "Preparing" : "Failed";
            dto.PreparationPercent = session.Percent;
            dto.DurationMs = session.DurationMs;
            dto.PlayUrl = session.Ready ? $"{ApiRoutes.Media}/{mediaId}/transcode/{session.Id}/media.mp4" : null;
            dto.MimeType = "video/mp4";
        }

        return dto;
    }

    private static MediaTrackInfo? DefaultAudio(MediaFileRecord file) =>
        file.AudioTracks.FirstOrDefault(t => t.IsDefault) ?? file.AudioTracks.FirstOrDefault();

    private static string? FolderName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? null : Path.GetFileName(directory);
    }

    /// <summary>MIME type Media3/ExoPlayer should be told when opening a file of this container.</summary>
    public static string ContentTypeFor(string? container)
    {
        var name = CodecNames.Normalize(container);
        return name switch
        {
            CodecNames.Mp4 => "video/mp4",
            CodecNames.Mkv => "video/x-matroska",
            CodecNames.Webm => "video/webm",
            CodecNames.Ts => "video/mp2t",
            CodecNames.MpegPs => "video/mpeg",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "m4a" => "audio/mp4",
            "aac" => "audio/aac",
            "opus" => "audio/opus",
            "ogg" => "audio/ogg",
            "wav" => "audio/wav",
            _ => string.IsNullOrWhiteSpace(name) ? "application/octet-stream" : $"video/{name}",
        };
    }

    // --- library / devices --------------------------------------------------

    public static FolderDto Folder(LibraryFolder folder, int mediaCount, long totalBytes, bool exists = true) => new()
    {
        Id = folder.Id,
        Path = folder.Path,
        Name = folder.Name,
        Type = folder.Type,
        Recursive = folder.Recursive,
        Enabled = folder.Enabled,
        WatchForChanges = folder.WatchForChanges,
        MediaCount = mediaCount,
        TotalBytes = totalBytes,
        AddedUtc = folder.AddedUtc,
        LastScanUtc = folder.LastScanUtc,
        Exists = exists,
    };

    public static TrustedDeviceDto Device(TrustedDevice device, bool online) => new()
    {
        Id = device.Id,
        DeviceId = device.DeviceId,
        Name = device.Name,
        Model = device.Model,
        Platform = device.Platform,
        ApprovedUtc = device.ApprovedUtc,
        LastSeenUtc = device.LastSeenUtc,
        Revoked = device.Revoked,
        Online = online,
        CapabilitiesSummary = CapabilitiesSummary(ParseCapabilities(device.CapabilitiesJson)),
    };

    public static DeviceCapabilities? ParseCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<DeviceCapabilities>(json, ApiJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One line codec summary for the PC device list ("HEVC/H264 · 1080p · HDR").</summary>
    public static string? CapabilitiesSummary(DeviceCapabilities? capabilities)
    {
        if (capabilities is null) return null;

        var parts = new List<string>();
        if (capabilities.VideoCodecs.Count > 0)
        {
            parts.Add(string.Join('/', capabilities.VideoCodecs.Take(4).Select(c => c.ToUpperInvariant())));
        }

        if (capabilities.MaxHeight is > 0) parts.Add($"{capabilities.MaxHeight}p");
        if (capabilities.Hdr10 || capabilities.Hlg || capabilities.DolbyVision) parts.Add("HDR");
        if (capabilities.MeasuredThroughputMbps is > 0)
        {
            parts.Add($"{capabilities.MeasuredThroughputMbps.Value:0.#} Mbps");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The read-only settings subset the Android client needs.</summary>
    public static ClientSettingsDto ClientSettings(LanternSettings settings, FfmpegTools tools) => new()
    {
        PcAudioOffsetMs = settings.PcAudioOffsetMs,
        TranscodingEnabled = settings.TranscodingEnabled && tools.CanTranscode,
        MaxStreamQuality = settings.MaxStreamQuality,
        ThumbnailsEnabled = settings.ThumbnailsEnabled,
        ServerName = string.IsNullOrWhiteSpace(settings.ServerName) ? "Lantern PC" : settings.ServerName,
        PcAudioEnabled = settings.PcAudioEnabled,
        MeasuredThroughputMbps = settings.LastMeasuredThroughputMbps > 0
            ? settings.LastMeasuredThroughputMbps
            : null,
        SubtitleLanguages = [],
        ApiVersion = "v1",
    };

    public static FfmpegInfoDto Ffmpeg(FfmpegTools tools) => new()
    {
        Available = tools.Available,
        Version = tools.Version,
        FfmpegPath = tools.FfmpegPath,
        FfprobePath = tools.FfprobePath,
        HardwareEncoders = [.. tools.HardwareEncoders],
        Error = tools.Error,
    };
}

namespace Lantern.Core.Dtos;

using Lantern.Core.Models;

/// <summary>Compact media entry used in lists/grids on Android.</summary>
public class MediaSummaryDto
{
    public long Id { get; set; }

    /// <summary>Opaque, stable stream token (encodes file + decision inputs).</summary>
    public required string StreamId { get; set; }

    public required string Title { get; set; }
    public int? Year { get; set; }
    public MediaKind Kind { get; set; }
    public string? ShowTitle { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeCode { get; set; }

    public long DurationMs { get; set; }
    public string DurationLabel => FormatTime(DurationMs);

    public int? Width { get; set; }
    public int? Height { get; set; }
    public string ResolutionLabel => Width is > 0 && Height is > 0 ? $"{Width} × {Height}" : "Unknown";

    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public long? BitrateBps { get; set; }
    public string BitrateLabel => BitrateBps is > 0 ? $"{BitrateBps.Value / 1_000_000.0:0.#} Mbps" : "—";

    public long SizeBytes { get; set; }
    public string? Container { get; set; }
    public string CodecLabel { get; set; } = string.Empty;
    public bool IsHdr { get; set; }

    public string? ThumbnailUrl { get; set; }
    public string? StreamUrl { get; set; }
    public string? FileName { get; set; }
    public string? FolderName { get; set; }

    public long PositionMs { get; set; }
    public double ProgressPercent { get; set; }
    public bool Completed { get; set; }

    public DateTime AddedUtc { get; set; }
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Featured badge text such as "4K HEVC" / "S01E04".</summary>
    public string Badge => Kind == MediaKind.Episode && !string.IsNullOrEmpty(EpisodeCode)
        ? EpisodeCode!
        : CodecLabel;

    public static string FormatTime(long ms)
    {
        if (ms <= 0) return "—";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

/// <summary>Full detail for the movie/episode screen.</summary>
public sealed class MediaDetailDto : MediaSummaryDto
{
    public string? Description { get; set; }
    public List<MediaFileDto> Files { get; set; } = [];
    public List<SubtitleDto> Subtitles { get; set; } = [];
    public List<AudioTrackDto> AudioTracks { get; set; } = [];
    public PlaybackPlanDto? Plan { get; set; }
    public MediaSummaryDto? NextEpisode { get; set; }
}

/// <summary>One physical file of a media item.</summary>
public sealed class MediaFileDto
{
    public long Id { get; set; }
    public required string FileName { get; set; }
    public required string Container { get; set; }
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
    public long? BitrateBps { get; set; }
    public bool IsHdr { get; set; }
    public string? DolbyVisionProfile { get; set; }
    public string? PixelFormat { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class AudioTrackDto
{
    public int Index { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public bool IsDefault { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool SupportedByDevice { get; set; }
}

public sealed class SubtitleDto
{
    public required string Id { get; set; }
    public required string Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsExternal { get; set; }
    public bool IsBitmap { get; set; }
    public string Label { get; set; } = string.Empty;

    /// <summary>Relative URL the client can hand to Media3.</summary>
    public string? Url { get; set; }

    /// <summary>Media3/Uri mime type hint (application/x-subrip, text/vtt, ...).</summary>
    public string? MimeType { get; set; }
}

/// <summary>The serialized <see cref="PlaybackPlan"/> plus the URLs to use for it.</summary>
public sealed class PlaybackPlanDto
{
    public PlaybackMode Mode { get; set; }
    public StreamDecision Decision { get; set; }
    public string ModeLabel { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = [];
    public string Container { get; set; } = "mp4";
    public string VideoAction { get; set; } = "copy";
    public string AudioAction { get; set; } = "copy";
    public string? OutputVideoCodec { get; set; }
    public string? OutputAudioCodec { get; set; }
    public int? OutputWidth { get; set; }
    public int? OutputHeight { get; set; }
    public long? OutputVideoBitrateBps { get; set; }
    public int? OutputAudioBitrateKbps { get; set; }
    public bool AudioOnly { get; set; }

    /// <summary>Direct file stream (HTTP range). Null when a session is required.</summary>
    public string? DirectStreamUrl { get; set; }

    /// <summary>HLS playlist URL for remux/transcode sessions.</summary>
    public string? HlsUrl { get; set; } // Legacy wire field; never populated.
    public string PreparationState { get; set; } = "Ready";
    public double? PreparationPercent { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>URL the client must use (direct stream or HLS playlist).</summary>
    public string? PlayUrl { get; set; }

    public string? SessionId { get; set; }
    public string? MimeType { get; set; }
}

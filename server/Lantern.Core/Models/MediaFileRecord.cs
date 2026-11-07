namespace Lantern.Core.Models;

/// <summary>
/// A single playable media file on disk (one row per file). A logical
/// <see cref="MediaItem"/> can own several files (e.g. 1080p + 4K versions, or
/// multiple parts of the same episode).
/// </summary>
public sealed class MediaFileRecord
{
    public long Id { get; set; }

    /// <summary>Owning logical media item.</summary>
    public long MediaId { get; set; }

    /// <summary>Absolute path on the server machine.</summary>
    public required string Path { get; set; }

    /// <summary>File name only, for display fallback.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    public string? Container { get; set; }
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }

    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FrameRate { get; set; }
    public long? BitrateBps { get; set; }
    public string? PixelFormat { get; set; }

    /// <summary>True when the video stream carries HDR metadata (HDR10 / HLG / DV profile detected).</summary>
    public bool IsHdr { get; set; }

    /// <summary>Detected Dolby Vision profile (e.g. "dvhe.05.06") when present.</summary>
    public string? DolbyVisionProfile { get; set; }

    /// <summary>Number of embedded audio streams.</summary>
    public int AudioStreamCount { get; set; }

    /// <summary>All embedded audio streams, ordered by stream index.</summary>
    public IReadOnlyList<MediaTrackInfo> AudioTracks { get; set; } = [];

    /// <summary>All embedded subtitle streams plus sibling subtitle files.</summary>
    public IReadOnlyList<SubtitleTrackInfo> SubtitleTracks { get; set; } = [];

    public DateTime LastWriteUtc { get; set; }
    public DateTime LastScannedUtc { get; set; }

    /// <summary>Width x height display helper.</summary>
    public string ResolutionLabel => Width is > 0 && Height is > 0 ? $"{Width} × {Height}" : "Unknown";

    /// <summary>Short label such as "4K HEVC".</summary>
    public string CodecLabel
    {
        get
        {
            var res = Height switch
            {
                >= 2000 => "4K",
                >= 1400 => "1440p",
                >= 1000 => "1080p",
                >= 700 => "720p",
                >= 500 => "576p",
                > 0 => "SD",
                _ => null,
            };
            var codec = VideoCodec?.ToUpperInvariant();
            return string.Join(' ', new[] { res, codec }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }
}

/// <summary>An audio (or video) stream inside a container.</summary>
public sealed class MediaTrackInfo
{
    public int Index { get; set; }
    public string? Codec { get; set; }
    public string? Profile { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public int? Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public int? SampleRate { get; set; }
    public long? BitrateBps { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }

    /// <summary>Human readable label such as "EAC3 5.1 (eng)".</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            else if (!string.IsNullOrWhiteSpace(Language)) parts.Add(Language!);
            parts.Add(Codec?.ToUpperInvariant() ?? "unknown");
            if (Channels is > 0) parts.Add(ChannelLabel(Channels.Value));
            return string.Join(' ', parts);
        }
    }

    private static string ChannelLabel(int channels) => channels switch
    {
        1 => "mono",
        2 => "2.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => $"{channels}ch",
    };
}

/// <summary>A subtitle track, either embedded in the container or an external sibling file.</summary>
public sealed class SubtitleTrackInfo
{
    /// <summary>Stable id used in URLs: "e:{streamIndex}" for embedded, "f:{fileName}" for external.</summary>
    public required string Id { get; set; }

    /// <summary>Format family: subrip, ass, ssa, webvtt, mov_text, pgs, dvd_subtitle ...</summary>
    public required string Codec { get; set; }

    /// <summary>Language tag (ISO 639-1/2) when known.</summary>
    public string? Language { get; set; }

    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }

    /// <summary>True when the track lives in a separate file beside the movie.</summary>
    public bool IsExternal { get; set; }

    /// <summary>Stream index for embedded tracks (null for external files).</summary>
    public int? StreamIndex { get; set; }

    /// <summary>Absolute path for external subtitle files.</summary>
    public string? ExternalPath { get; set; }

    /// <summary>True when the track is image based (PGS/VobSub) and needs bitmap rendering (unsupported by Media3).</summary>
    public bool IsBitmapBased => Codec.Equals("hdmv_pgs_subtitle", StringComparison.OrdinalIgnoreCase)
        || Codec.Equals("pgs", StringComparison.OrdinalIgnoreCase)
        || Codec.Equals("dvd_subtitle", StringComparison.OrdinalIgnoreCase)
        || Codec.Equals("dvb_subtitle", StringComparison.OrdinalIgnoreCase)
        || Codec.Equals("xsub", StringComparison.OrdinalIgnoreCase);

    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Language)) parts.Add(Language!.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            if (IsForced) parts.Add("forced");
            if (IsExternal) parts.Add("external");
            parts.Add(Codec.ToUpperInvariant());
            return string.Join(" · ", parts);
        }
    }
}

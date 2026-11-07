using Lantern.Core.Models;

namespace Lantern.Media.Probing;

/// <summary>The technical facts about one video file, mapped from ffprobe output.</summary>
public sealed class MediaProbeResult
{
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
    public bool IsHdr { get; set; }
    public string? DolbyVisionProfile { get; set; }
    public bool HasVideoStream { get; set; }

    public List<MediaTrackInfo> AudioTracks { get; set; } = [];
    public List<SubtitleTrackInfo> SubtitleTracks { get; set; } = [];

    /// <summary>Set when probing failed; the file can still be listed but not planned.</summary>
    public string? Error { get; set; }

    public bool Succeeded => Error is null;

    /// <summary>Average of all audio bitrates when the format bitrate is unknown.</summary>
    internal static long? SumAudioBitrate(IEnumerable<FfprobeStream> streams)
    {
        long total = 0;
        var any = false;
        foreach (var s in streams)
        {
            if (FfprobeFormat.ParseLong(s.BitRate) is { } bits and > 0)
            {
                total += bits;
                any = true;
            }
        }
        return any ? total : null;
    }

    /// <summary>Projects the probe result onto the persisted file record.</summary>
    public MediaFileRecord ToRecord(long mediaId, string path, DateTime lastWriteUtc, DateTime scannedUtc)
    {
        var record = new MediaFileRecord
        {
            MediaId = mediaId,
            Path = path,
            Container = Container,
            SizeBytes = SizeBytes,
            DurationMs = DurationMs,
            VideoCodec = VideoCodec,
            AudioCodec = AudioCodec,
            Width = Width,
            Height = Height,
            FrameRate = FrameRate,
            BitrateBps = BitrateBps,
            PixelFormat = PixelFormat,
            IsHdr = IsHdr,
            DolbyVisionProfile = DolbyVisionProfile,
            AudioStreamCount = AudioTracks.Count,
            AudioTracks = AudioTracks,
            SubtitleTracks = SubtitleTracks,
            LastWriteUtc = lastWriteUtc,
            LastScannedUtc = scannedUtc,
        };
        return record;
    }
}

namespace Lantern.Core.Dtos;

using Lantern.Core.Models;

/// <summary>POST /api/v1/playback/progress – client reports where it is.</summary>
public sealed class ProgressReportDto
{
    public long MediaId { get; set; }

    /// <summary>Device id; the server also knows it from the auth token.</summary>
    public string? ClientId { get; set; }

    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public PlaybackState State { get; set; } = PlaybackState.Playing;
    public float PlaybackRate { get; set; } = 1.0f;

    /// <summary>How much media is buffered ahead of the playhead (diagnostics).</summary>
    public long BufferedMs { get; set; }

    /// <summary>Current stream bitrate in bps as observed by the client (diagnostics).</summary>
    public long? ObservedBitrateBps { get; set; }

    public AudioMode AudioMode { get; set; } = AudioMode.Projector;
    public int DroppedFrames { get; set; }
    public string? DecoderName { get; set; }

    /// <summary>User pressed "start over" – clears the stored position.</summary>
    public bool Reset { get; set; }

    /// <summary>Marks the item as watched (or not) explicitly.</summary>
    public bool? Completed { get; set; }
}

/// <summary>Response of POST /api/v1/playback/progress.</summary>
public sealed class ProgressAckDto
{
    public long MediaId { get; set; }
    public long PositionMs { get; set; }
    public double Percent { get; set; }
    public bool Completed { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Server-side audio plan for this client (PC audio mode needs the server to know).</summary>
    public AudioMode AudioMode { get; set; }
    public int PcAudioOffsetMs { get; set; }
}

/// <summary>Resume information for one media item.</summary>
public sealed class ResumeDto
{
    public long MediaId { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public double Percent { get; set; }
    public bool Completed { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string? MediaTitle { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool ShouldResume => !Completed && PositionMs > 15_000 && Percent < 97;
    public string PositionLabel => MediaSummaryDto.FormatTime(PositionMs);
}

/// <summary>Request used by the player screen to ask for a plan before playing.</summary>
public sealed class PlaybackPlanRequestDto
{
    public long? MediaId { get; set; }
    public string? StreamId { get; set; }
    public StreamQuality Quality { get; set; } = StreamQuality.Auto;
    public bool ForceTranscode { get; set; }
    public bool ForceRemux { get; set; }
    public bool AudioOnly { get; set; }
    public AudioMode AudioMode { get; set; } = AudioMode.Projector;
    public int? AudioTrackIndex { get; set; }

    /// <summary>Override: force a specific start position (seconds) for transcode sessions.</summary>
    public double? StartSeconds { get; set; }
}

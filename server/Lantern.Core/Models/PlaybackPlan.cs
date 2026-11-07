namespace Lantern.Core.Models;

/// <summary>Inputs that influence the direct-play / transcode decision.</summary>
public sealed class CompatibilityOptions
{
    /// <summary>When false, unsupported files are reported as unplayable instead of being transcoded.</summary>
    public bool TranscodingEnabled { get; set; } = true;

    /// <summary>Allow stream copy (remux) when only the container is the problem.</summary>
    public bool AllowRemux { get; set; } = true;

    /// <summary>Upper bound for transcoded output quality (Auto = no user cap).</summary>
    public StreamQuality MaxQuality { get; set; } = StreamQuality.Auto;

    /// <summary>Hard cap in Mbps for the streamed video bitrate (0 = unlimited, LAN default).</summary>
    public double MaxBitrateMbps { get; set; }

    /// <summary>Hard cap for the output height (0 = unlimited).</summary>
    public int MaxHeightCap { get; set; }

    /// <summary>Preferred H.264 encoder for transcodes (auto/libx264/h264_nvenc/h264_qsv/h264_amf).</summary>
    public string VideoEncoder { get; set; } = "auto";

    public static CompatibilityOptions FromSettings(LanternSettings s) => new()
    {
        TranscodingEnabled = s.TranscodingEnabled,
        AllowRemux = true,
        MaxQuality = s.MaxStreamQuality,
        MaxBitrateMbps = s.MaxBitrateMbps,
        VideoEncoder = s.TranscodeEncoder,
    };
}

/// <summary>
/// The result of evaluating a media file against a device profile. Everything the
/// server needs to answer "how do I play this?" lives here; the client renders it
/// as human readable text and decides which URL to open.
/// </summary>
public sealed class PlaybackPlan
{
    public PlaybackMode Mode { get; set; } = PlaybackMode.DirectPlay;
    public StreamDecision Decision { get; set; } = StreamDecision.DirectPlay;

    /// <summary>Human readable explanation shown in the player UI.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Additional technical notes (codec mismatch details, quality caps, ...).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>Container used for the stream: the original one for direct play, otherwise mkv/hls.</summary>
    public string Container { get; set; } = "mp4";

    /// <summary>"copy" or "transcode".</summary>
    public string VideoAction { get; set; } = "copy";

    /// <summary>"copy", "transcode" or "none".</summary>
    public string AudioAction { get; set; } = "copy";

    /// <summary>Video codec of the output stream (source codec when copied).</summary>
    public string? OutputVideoCodec { get; set; }

    /// <summary>Audio codec of the output stream (source codec when copied).</summary>
    public string? OutputAudioCodec { get; set; }

    public int? OutputWidth { get; set; }
    public int? OutputHeight { get; set; }

    /// <summary>Bitrate cap in bps applied by the transcoder (null = CRF/quality based, no cap).</summary>
    public long? OutputVideoBitrateBps { get; set; }

    public int? OutputAudioBitrateKbps { get; set; }

    /// <summary>True when playback must not be started at 0 but from the requested offset.</summary>
    public bool SupportsSeek { get; set; } = true;

    /// <summary>True when the client must use the HLS transcode endpoint rather than the file endpoint.</summary>
    public bool RequiresSession => Mode != PlaybackMode.DirectPlay;

    /// <summary>Selected embedded audio stream index, when the decision keeps a source track.</summary>
    public int? SelectedAudioStreamIndex { get; set; }

    /// <summary>True when the plan dedicates the stream to audio only (PC audio renderer or audio-only client).</summary>
    public bool AudioOnly { get; set; }

    public string ModeLabel => Mode switch
    {
        PlaybackMode.DirectPlay => "Direct Play",
        PlaybackMode.Remux => "Remux",
        _ => "Transcoding",
    };
}

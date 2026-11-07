namespace Lantern.Core.Models;

/// <summary>Server-settings that are persisted in SQLite and editable from the PC UI.</summary>
public sealed class LanternSettings
{
    /// <summary>HTTP port the server listens on.</summary>
    public int Port { get; set; } = 8787;

    /// <summary>Bind address. 0.0.0.0 (default) listens on every interface including the LAN.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Friendly server name advertised over mDNS/UDP.</summary>
    public string ServerName { get; set; } = "Lantern PC";

    /// <summary>Path to ffmpeg executable (folder or full path). Empty = auto detect.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Path to ffprobe executable (folder or full path). Empty = auto detect next to ffmpeg.</summary>
    public string FfprobePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the MediaMTX relay that carries screen mirroring (folder or full path).
    /// Empty = auto detect beside the server (mediamtx\mediamtx.exe), then tools\mediamtx, then PATH.
    /// It is not an application the user runs: the server starts it with the session and stops it after.
    /// </summary>
    public string MediaMtxPath { get; set; } = string.Empty;

    public bool EnableMdns { get; set; } = true;
    public bool EnableUdpBeacon { get; set; } = true;

    /// <summary>Require explicit device approval before any API access.</summary>
    public bool RequirePairing { get; set; } = true;

    /// <summary>Allow transcoding when direct play is not possible.</summary>
    public bool TranscodingEnabled { get; set; } = true;

    public int TranscodeCacheMaxGb { get; set; } = 10;

    /// <summary>Preferred transcoder: auto, libx264, h264_nvenc, h264_qsv, h264_amf.</summary>
    public string TranscodeEncoder { get; set; } = "auto";

    /// <summary>Upper quality cap for transcoded video.</summary>
    public StreamQuality MaxStreamQuality { get; set; } = StreamQuality.Auto;

    /// <summary>Optional hard cap on streamed/transcoded video bitrate in Mbps (0 = unlimited, LAN default).</summary>
    public double MaxBitrateMbps { get; set; } = 0;

    /// <summary>Generate thumbnails with ffmpeg.</summary>
    public bool ThumbnailsEnabled { get; set; } = true;

    /// <summary>Skip thumbnail generation for files larger than this many GB (0 = no limit).</summary>
    public double ThumbnailMaxFileSizeGb { get; set; } = 0;

    /// <summary>Rescan folders automatically using filesystem watchers.</summary>
    public bool WatchFolders { get; set; } = true;

    /// <summary>Scan folders on server start.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>Where thumbnails/metadata cache is written. Empty = %LOCALAPPDATA%\Lantern\cache.</summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>SQLite database file. Empty = %LOCALAPPDATA%\Lantern\lantern.db.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Write verbose logs (per-request details, transcode commands).</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Audio is rendered on the PC (Bluetooth speaker) instead of the projector.</summary>
    public bool PcAudioEnabled { get; set; }

    /// <summary>Audio/video sync offset applied by the PC audio renderer in milliseconds.</summary>
    public int PcAudioOffsetMs { get; set; }

    /// <summary>Stereo downmix for PC audio output.</summary>
    public int PcAudioChannels { get; set; } = 2;

    /// <summary>Index of the Windows output device used for PC audio (-1 = system default).</summary>
    public int PcAudioDeviceIndex { get; set; } = -1;

    /// <summary>Estimated LAN throughput in Mbps measured by the last speed test (0 = not measured).</summary>
    public double LastMeasuredThroughputMbps { get; set; }

    /// <summary>Percentage of measured throughput considered safe for a single stream.</summary>
    public double ThroughputSafetyFactor { get; set; } = 0.6;

    /// <summary>Seconds a finished item must be watched before being marked complete.</summary>
    public double CompletedThresholdPercent { get; set; } = 95;
}

namespace Lantern.Core.Dtos;

/// <summary>Response of GET /api/v1/server/info. Available without authentication.</summary>
public sealed class ServerInfoDto
{
    public required string ServerId { get; set; }
    public required string Name { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string ApiVersion { get; set; } = "v1";
    public string MachineName { get; set; } = Environment.MachineName;
    public string Os { get; set; } = Environment.OSVersion.ToString();
    public int Port { get; set; }
    public List<string> Addresses { get; set; } = [];

    /// <summary>Address the client should use, best effort (LAN address of the request interface).</summary>
    public string? PrimaryAddress { get; set; }

    public DateTime StartedUtc { get; set; }
    public bool RequiresPairing { get; set; }
    public long UptimeSeconds { get; set; }
    public ServerFeaturesDto Features { get; set; } = new();
    public LibraryCountsDto Library { get; set; } = new();
    public FfmpegInfoDto Ffmpeg { get; set; } = new();
    public double? MeasuredThroughputMbps { get; set; }
}

public sealed class ServerFeaturesDto
{
    public bool Mdns { get; set; }
    public bool UdpBeacon { get; set; }
    public bool Transcoding { get; set; }
    public bool Thumbnails { get; set; }
    public bool Subtitles { get; set; }
    public bool PcAudio { get; set; }
    public bool WebSocket { get; set; } = true;
    public bool ResumeSync { get; set; } = true;
    public List<string> StreamingProtocols { get; set; } = ["http-range", "hls"];
}

public sealed class LibraryCountsDto
{
    public int Movies { get; set; }
    public int Episodes { get; set; }
    public int Shows { get; set; }
    public int Folders { get; set; }
    public int TotalItems { get; set; }
    public long TotalBytes { get; set; }
    public DateTime? LastScanUtc { get; set; }
}

public sealed class FfmpegInfoDto
{
    public bool Available { get; set; }

    /// <summary>"ffmpeg version 2024-12-04-..." when detected.</summary>
    public string? Version { get; set; }

    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public List<string> HardwareEncoders { get; set; } = [];
    public string? Error { get; set; }
}

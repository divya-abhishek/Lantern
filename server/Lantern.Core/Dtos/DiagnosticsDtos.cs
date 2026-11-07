namespace Lantern.Core.Dtos;

using Lantern.Core.Models;

/// <summary>GET /api/v1/diagnostics – everything the Android diagnostics screen shows.</summary>
public sealed class DiagnosticsDto
{
    public required string ServerId { get; set; }
    public required string ServerName { get; set; }
    public string Version { get; set; } = "1.0.0";
    public DateTime StartedUtc { get; set; }
    public long UptimeSeconds { get; set; }

    public List<string> ServerAddresses { get; set; } = [];
    public int Port { get; set; }
    public string? ClientIp { get; set; }
    public string? ClientUserAgent { get; set; }

    public int ConnectedClients { get; set; }
    public List<ClientConnectionDto> Clients { get; set; } = [];

    public double CpuPercent { get; set; }
    public long ProcessMemoryMb { get; set; }
    public long ManagedMemoryMb { get; set; }
    public int ThreadCount { get; set; }

    public double? MeasuredThroughputMbps { get; set; }
    public double? RecommendedStreamBitrateMbps { get; set; }
    public double ThroughputSafetyFactor { get; set; }

    public FfmpegInfoDto Ffmpeg { get; set; } = new();
    public List<TranscodeSessionDto> TranscodingSessions { get; set; } = [];
    public List<StreamStatDto> ActiveStreams { get; set; } = [];

    public LibraryCountsDto Library { get; set; } = new();
    public ScanStatusDto Scanning { get; set; } = new();

    public AudioMode AudioMode { get; set; }
    public bool PcAudioAvailable { get; set; }
    public bool PcAudioRunning { get; set; }
    public int PcAudioOffsetMs { get; set; }
    public double? PcAudioDriftMs { get; set; }

    public List<LogEntryDto> RecentErrors { get; set; } = [];
}

public sealed class ClientConnectionDto
{
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public string? IpAddress { get; set; }
    public DateTime ConnectedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool WebSocketConnected { get; set; }
    public string? Platform { get; set; }
}

public sealed class TranscodeSessionDto
{
    public required string SessionId { get; set; }
    public long MediaId { get; set; }
    public required string Title { get; set; }
    public PlaybackMode Mode { get; set; }
    public string? VideoAction { get; set; }
    public string? AudioAction { get; set; }
    public double StartSeconds { get; set; }
    public DateTime StartedUtc { get; set; }
    public long OutputBytes { get; set; }
    public double? Speed { get; set; }
    public double? Fps { get; set; }
    public long? OutputBitrateBps { get; set; }
    public bool Running { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastAccessUtc { get; set; }
}

public sealed class StreamStatDto
{
    public long MediaId { get; set; }
    public required string Title { get; set; }
    public required string DeviceId { get; set; }
    public DateTime StartedUtc { get; set; }
    public long BytesSent { get; set; }
    public long DurationMs { get; set; }
    public double AverageMbps { get; set; }
    public double InstantMbps { get; set; }
    public bool DirectPlay { get; set; }
    public long RangeRequests { get; set; }
    public DateTime LastActivityUtc { get; set; }
}

/// <summary>Result of GET /api/v1/diagnostics/speedtest (PC -> client throughput probe).</summary>
public sealed class SpeedTestResultDto
{
    public long Bytes { get; set; }
    public double ElapsedMs { get; set; }
    public double Mbps { get; set; }
    public double RecommendedStreamBitrateMbps { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class LogEntryDto
{
    public DateTime TimestampUtc { get; set; }
    public required string Level { get; set; }
    public required string Category { get; set; }
    public required string Message { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Server settings exposed to the Android client (read only subset).</summary>
public sealed class ClientSettingsDto
{
    public int PcAudioOffsetMs { get; set; }
    public bool TranscodingEnabled { get; set; }
    public StreamQuality MaxStreamQuality { get; set; }
    public bool ThumbnailsEnabled { get; set; }
    public string ServerName { get; set; } = "Lantern PC";
    public bool PcAudioEnabled { get; set; }
    public double? MeasuredThroughputMbps { get; set; }
    public List<string> SubtitleLanguages { get; set; } = [];
    public string ApiVersion { get; set; } = "v1";
}

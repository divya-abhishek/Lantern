using System.Text.Json.Serialization;

namespace Lantern.Core.Messaging;

using Lantern.Core.Dtos;
using Lantern.Core.Models;

/// <summary>
/// Base type for every WebSocket message. The JSON discriminator is the "type"
/// string, which both the C# and the Kotlin client use for dispatch.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClientHelloMessage), "client.hello")]
[JsonDerivedType(typeof(ServerHelloMessage), "server.hello")]
[JsonDerivedType(typeof(PlaybackReportMessage), "playback.report")]
[JsonDerivedType(typeof(PlaybackStateMessage), "playback.state")]
[JsonDerivedType(typeof(PlaybackCommandMessage), "playback.command")]
[JsonDerivedType(typeof(DeviceListMessage), "devices.list")]
[JsonDerivedType(typeof(LibraryChangedMessage), "library.changed")]
[JsonDerivedType(typeof(ScanStatusMessage), "scan.status")]
[JsonDerivedType(typeof(TranscodeStatusMessage), "transcode.status")]
[JsonDerivedType(typeof(PcAudioStateMessage), "pc.audio.state")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract class LanternMessage
{
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>First message a client sends after the socket opens.</summary>
public sealed class ClientHelloMessage : LanternMessage
{
    public required string DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public DeviceCapabilities? Capabilities { get; set; }
    public double? MeasuredThroughputMbps { get; set; }

    /// <summary>Media id the client is currently showing (for the PC UI).</summary>
    public long? CurrentMediaId { get; set; }
}

public sealed class ServerHelloMessage : LanternMessage
{
    public required string ServerId { get; set; }
    public required string ServerName { get; set; }
    public string Version { get; set; } = "1.0.0";
    public ClientSettingsDto Settings { get; set; } = new();
    public LibraryCountsDto Library { get; set; } = new();
    public int HeartbeatIntervalMs { get; set; } = 2000;
    public List<string> Capabilities { get; set; } = ["resume", "hls", "pc-audio", "remote"];
}

/// <summary>Client -> server: periodic playback heartbeat used for resume + PC audio sync.</summary>
public sealed class PlaybackReportMessage : LanternMessage
{
    public long MediaId { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public PlaybackState State { get; set; } = PlaybackState.Playing;
    public float PlaybackRate { get; set; } = 1.0f;
    public long BufferedMs { get; set; }
    public long? ObservedBitrateBps { get; set; }
    public int DroppedFrames { get; set; }
    public string? DecoderName { get; set; }
    public AudioMode AudioMode { get; set; } = AudioMode.Projector;
    public PlaybackMode? Mode { get; set; }
    public bool Reset { get; set; }
}

/// <summary>Server -> all clients: authoritative playback state (used by the PC UI + remote).</summary>
public sealed class PlaybackStateMessage : LanternMessage
{
    public required string DeviceId { get; set; }
    public long? MediaId { get; set; }
    public string? Title { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public PlaybackState State { get; set; } = PlaybackState.Idle;
    public float PlaybackRate { get; set; } = 1.0f;
    public long BufferedMs { get; set; }
    public PlaybackMode? Mode { get; set; }
    public AudioMode AudioMode { get; set; } = AudioMode.Projector;
    public string? Reason { get; set; }

    /// <summary>Media time minus audio-renderer time, filled when PC audio is active.</summary>
    public double? PcAudioDriftMs { get; set; }
}

/// <summary>Bidirectional control message (remote control + PC-driving playback).</summary>
public sealed class PlaybackCommandMessage : LanternMessage
{
    public PlaybackCommand Command { get; set; } = PlaybackCommand.Play;
    public long? MediaId { get; set; }
    public long? PositionMs { get; set; }
    public float? Rate { get; set; }
    public double? Volume { get; set; }
    public AudioMode? AudioMode { get; set; }
    public string? SubtitleId { get; set; }
    public int? AudioTrackIndex { get; set; }
    public StreamQuality? Quality { get; set; }
    public string? IssuedBy { get; set; }
    public string? Message { get; set; }
}

public enum PlaybackCommand
{
    Play = 0,
    Pause = 1,
    TogglePlayPause = 2,
    Seek = 3,
    Stop = 4,
    SetRate = 5,
    SetVolume = 6,
    SetAudioMode = 7,
    SetSubtitle = 8,
    SetAudioTrack = 9,
    SetQuality = 10,
    Next = 11,
    Previous = 12,
}

public sealed class DeviceListMessage : LanternMessage
{
    public List<TrustedDeviceDto> Devices { get; set; } = [];
    public List<ClientConnectionDto> Online { get; set; } = [];
}

public sealed class LibraryChangedMessage : LanternMessage
{
    public LibraryCountsDto Counts { get; set; } = new();
    public List<long>? ChangedMediaIds { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
}

public sealed class ScanStatusMessage : LanternMessage
{
    public required ScanStatusDto Status { get; set; }
}

public sealed class TranscodeStatusMessage : LanternMessage
{
    public required TranscodeSessionDto Session { get; set; }
}

public sealed class PcAudioStateMessage : LanternMessage
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public int OffsetMs { get; set; }
    public double? DriftMs { get; set; }
    public long? PositionMs { get; set; }
    public string? DeviceName { get; set; }
    public string? Message { get; set; }
}

public sealed class PingMessage : LanternMessage
{
    public long Ticks { get; set; }
}

public sealed class PongMessage : LanternMessage
{
    public long Ticks { get; set; }
    public long ClientTicks { get; set; }
}

public sealed class ErrorMessage : LanternMessage
{
    public required string Code { get; set; }
    public required string Message { get; set; }
    public string? Detail { get; set; }
    public bool Fatal { get; set; }
}

namespace Lantern.Core.Models;

/// <summary>What kind of media an item represents.</summary>
public enum MediaKind
{
    Movie = 0,
    Episode = 1,
    Other = 2,
}

/// <summary>State of a playback session as reported by the client.</summary>
public enum PlaybackState
{
    Idle = 0,
    Buffering = 1,
    Playing = 2,
    Paused = 3,
    Ended = 4,
}

/// <summary>Where audio should be rendered.</summary>
public enum AudioMode
{
    /// <summary>Audio is decoded and played by the Android projector (default, most stable).</summary>
    Projector = 0,

    /// <summary>Video is played muted on the projector, audio is rendered by the PC (e.g. Bluetooth speaker).</summary>
    Pc = 1,
}

/// <summary>High level playback strategy chosen for a media file.</summary>
public enum PlaybackMode
{
    /// <summary>The original file is streamed untouched (HTTP range, full quality).</summary>
    DirectPlay = 0,

    /// <summary>Container/streams are re-packaged without re-encoding (e.g. copy video, transcode audio).</summary>
    Remux = 1,

    /// <summary>One or more streams must be re-encoded (HLS session on the server).</summary>
    Transcode = 2,
}

/// <summary>The concrete compatibility decision for (file, device).</summary>
public enum StreamDecision
{
    DirectPlay = 0,
    TranscodeVideo = 1,
    TranscodeAudio = 2,
    TranscodeBoth = 3,
}

/// <summary>Quality ladder used when transcoding.</summary>
public enum StreamQuality
{
    Auto = 0,
    Original = 1,
    High = 2,
    Medium = 3,
    Low = 4,
}

/// <summary>What a screen mirroring session captures.</summary>
public enum MirrorSource
{
    /// <summary>The whole virtual desktop (every attached display side by side).</summary>
    Desktop = 0,

    /// <summary>A single display, addressed by <c>DisplayIndex</c>.</summary>
    Display = 1,
}

/// <summary>Lifecycle of the screen mirroring pipeline.</summary>
public enum MirrorState
{
    /// <summary>Nothing is capturing.</summary>
    Idle = 0,

    /// <summary>ffmpeg is being started (capture device + encoder).</summary>
    Starting = 1,

    /// <summary>Frames are being captured, encoded and fanned out to receivers.</summary>
    Live = 2,

    /// <summary>The session is being torn down.</summary>
    Stopping = 3,

    /// <summary>The session ended on its own (encoder died, capture refused).</summary>
    Failed = 4,
}


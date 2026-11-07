namespace Lantern.Core.Dtos;

using Lantern.Core.Api;
using Lantern.Core.Models;

/// <summary>One display the PC can capture, as measured on that machine.</summary>
public sealed class MirrorDisplayDto
{
    /// <summary>Zero based capture index (ffmpeg <c>output_idx</c>); the value clients send back.</summary>
    public int Index { get; set; }

    /// <summary>Device name from Windows, e.g. <c>DISPLAY4</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public bool Primary { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

/// <summary>Settings for one screen mirroring session.</summary>
public sealed class MirrorSettingsDto
{
    public MirrorSource Source { get; set; } = MirrorSource.Desktop;

    /// <summary>Index into the displays from <c>GET /mirror/capabilities</c> when <see cref="Source"/> is Display.</summary>
    public int DisplayIndex { get; set; }

    /// <summary>Output width. 1280 and 1920 are the supported targets.</summary>
    public int Width { get; set; } = 1280;

    /// <summary>Output height. 720 and 1080 are the supported targets.</summary>
    public int Height { get; set; } = 720;

    /// <summary>30 or 60.</summary>
    public int Fps { get; set; } = 30;

    /// <summary>Low / Medium / High. Shares the transcoder's quality ladder.</summary>
    public StreamQuality Quality { get; set; } = StreamQuality.Medium;

    /// <summary>Capture Windows system audio (WASAPI loopback) and mux it as AAC.</summary>
    public bool Audio { get; set; } = true;
}

/// <summary>
/// What this PC can actually do, measured instead of assumed: the displays Windows reports,
/// the capture backends ffmpeg offers, the H.264 encoders that successfully encode a test frame
/// and whether system audio can be captured.
/// </summary>
public sealed class MirrorCapabilitiesDto
{
    /// <summary>False on a non-Windows host: desktop capture has no meaning there.</summary>
    public bool PlatformSupported { get; set; }

    public bool FfmpegAvailable { get; set; }

    public string? FfmpegPath { get; set; }

    public string? UnsupportedReason { get; set; }

    /// <summary>Backend that will be used: <c>ddagrab</c> (Desktop Duplication / Windows Graphics Capture) or <c>gdigrab</c>.</summary>
    public string? CaptureBackend { get; set; }

    /// <summary>Every capture backend ffmpeg exposes on this machine, best first.</summary>
    public List<string> CaptureBackends { get; set; } = [];

    /// <summary>
    /// The RTSP relay (MediaMTX) this PC will publish into. Mirroring cannot start without it: it is the
    /// transport, not an optional extra, which is why it is reported here next to ffmpeg.
    /// </summary>
    public bool RelayAvailable { get; set; }

    public string? RelayPath { get; set; }

    /// <summary>First line of <c>mediamtx --version</c>, e.g. "MediaMTX v1.21.0".</summary>
    public string? RelayVersion { get; set; }

    /// <summary>Why the relay is unavailable, when it is.</summary>
    public string? RelayReason { get; set; }

    public List<MirrorDisplayDto> Displays { get; set; } = [];

    /// <summary>H.264 encoder names that were verified by encoding, hardware first.</summary>
    public List<string> Encoders { get; set; } = [];

    public string? SelectedEncoder { get; set; }

    public bool HardwareEncoding { get; set; }

    /// <summary>True when Windows system audio can be captured on this machine.</summary>
    public bool AudioSupported { get; set; }

    public string? AudioDevice { get; set; }

    /// <summary>Why audio capture is unavailable, when it is.</summary>
    public string? AudioReason { get; set; }

    public int DefaultWidth { get; set; } = 1280;

    public int DefaultHeight { get; set; } = 720;

    public int DefaultFps { get; set; } = 30;

    /// <summary>
    /// The frame rates this server accepts, in the order they are offered, with the first one as the
    /// default. 30 and 60 on every build.
    ///
    /// Published rather than left for the client to guess because two clients now have to offer the same
    /// ladder the server will accept: a Windows pill that says 60 while the server answers 30 is exactly
    /// the mismatch this field exists to make impossible. A requested rate is snapped onto this ladder
    /// (see the server's <c>MirrorProfiles.NormalizeFrameRate</c>), so a client that sends 59 still lands
    /// on 60.
    /// </summary>
    public List<int> FrameRates { get; set; } = [];

    /// <summary>
    /// The highest rate on <see cref="FrameRates"/> that the selected encoder really encoded on this
    /// machine, measured with a test encode at <see cref="FrameRateProbeLabel"/>. It is the measured half
    /// of the ladder: 60 is offered, and this says whether 60 was achieved here.
    ///
    /// Zero means the check never ran (no encoder passed its own probe), which is why the UI reports it as
    /// unknown rather than as a failure.
    /// </summary>
    public int VerifiedMaxFrameRate { get; set; }

    /// <summary>What the rate check encoded, e.g. "1920 × 1080 @ 60". Null when it did not run.</summary>
    public string? FrameRateProbeLabel { get; set; }

    /// <summary>
    /// The outcome of the rate check in words: the encoder that ran it, or that encoder's own error when
    /// it refused the target. Reported instead of a bare number so a refusal is readable.
    /// </summary>
    public string? FrameRateReason { get; set; }

    /// <summary>True when a session can actually be started with the reported values.</summary>
    public bool Ready => PlatformSupported && FfmpegAvailable && SelectedEncoder is not null
                         && Displays.Count > 0 && RelayAvailable;
}

/// <summary>Live state of the mirroring pipeline; polled by the PC UI and the Android overlay.</summary>
public sealed class MirrorStatusDto
{
    public bool Active { get; set; }

    public MirrorState State { get; set; } = MirrorState.Idle;

    public MirrorSettingsDto? Settings { get; set; }

    /// <summary>Human readable capture target, e.g. "Display 1 · 1920 × 1080 · Primary".</summary>
    public string? SourceLabel { get; set; }

    public string? Encoder { get; set; }

    public string? CaptureBackend { get; set; }

    public bool HardwareEncoding { get; set; }

    public bool Audio { get; set; }

    public string? AudioDevice { get; set; }

    /// <summary>
    /// Milliseconds of silence inserted into the audio track because the loopback device delivered
    /// nothing (a Bluetooth speaker that went to sleep, for example). Non-zero means the picture is
    /// fine and the sound was silent for a while.
    /// </summary>
    public long AudioSilenceMs { get; set; }

    /// <summary>
    /// How old the sound handed to the encoder was, in milliseconds.
    ///
    /// The audio timeline is built from the sample count, so timestamps can be perfectly aligned while
    /// the sound itself is seconds behind the picture. This is the number that catches that: it stays
    /// under a quarter of a second in a healthy session, because the audio queue is trimmed rather than
    /// written late.
    /// </summary>
    public int AudioLagMs { get; set; }

    /// <summary>Bitrate the encoder was told to hit.</summary>
    public int TargetBitrateKbps { get; set; }

    /// <summary>Bitrate actually leaving the server towards the receivers (measured over the last second).</summary>
    public int MeasuredBitrateKbps { get; set; }

    public double FramesPerSecond { get; set; }

    /// <summary>
    /// The age of the newest packet this PC's capture-encode-mux-publish chain has written, measured from
    /// ffmpeg's own output timestamp against the same wall clock the picture is stamped with. It is a real
    /// measurement, and it is <b>not</b> glass-to-glass latency: the relay, the network, the decoder and
    /// the panel are not measurable from here and are not claimed.
    ///
    /// Measured on the development machine through RTSP: 0.8 s with the sound detached and 5.2 s with the
    /// WASAPI-fed audio stream attached, flat in both cases over sessions of 90 s and more. The 4.4 s
    /// difference is a property of the publisher's own pipeline and could not be traced to a specific
    /// buffer within the work: what was ruled out by measurement is the audio's content age (under 250 ms),
    /// the capture source's pacing (300 frames in 10.36 s on a still desktop), the muxer's interleave bound,
    /// and the stamping method (a wall-clock variant of the audio's clock changed it not at all). What it
    /// does *not* do is grow - <see cref="DriftMs"/> stays inside tens of milliseconds - which is the
    /// property this feature needs.
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// How much that latency has moved since the session's first frame. Near zero means the pipeline is
    /// running at a constant delay; a number that climbs means latency is accumulating, which is the
    /// failure the move away from the old transport is meant to prevent.
    /// </summary>
    public int DriftMs { get; set; }

    /// <summary>
    /// Milliseconds the relay is still holding for the slowest receiver.
    ///
    /// Always zero with RTSP, and that is a limit rather than a measurement: the relay keeps its own
    /// per-receiver queues and does not report their depth, so the PC can no longer see how far behind
    /// a receiver is. The receiver measures that itself - the Android mirror screen shows its own
    /// distance from the live edge and re-joins the stream when it drifts - and what the PC can see is
    /// reported instead: <see cref="Subscribers"/> and <see cref="ReaderDiscardedPackets"/>.
    /// </summary>
    public int BufferedMs { get; set; }

    public int Subscribers { get; set; }

    /// <summary>
    /// RTP packets the relay threw away for receivers that could not keep up. Zero is the healthy
    /// number: it means every receiver was draining the stream as fast as it arrived.
    /// </summary>
    public long ReaderDiscardedPackets { get; set; }

    public long FramesSent { get; set; }

    /// <summary>
    /// Always zero under RTSP, and the same limit <see cref="BufferedMs"/> describes: this counter came
    /// from scanning the MPEG-TS bytes the server used to write itself, and the server no longer sees the
    /// packets - the relay does. It is kept in the contract so an older client does not lose a field, and
    /// <see cref="ReaderDiscardedPackets"/> is what replaces it. Do not read a zero here as proof that
    /// there were no keyframes.
    /// </summary>
    public long KeyframesSeen { get; set; }

    public long DroppedBytes { get; set; }

    /// <summary>
    /// Bytes of system audio the loopback capture had to discard because the encoder could not take
    /// them fast enough. The audio timeline is preserved with silence in their place - that is what
    /// keeps the sound where the picture is - so this counter is the only visible trace of it, and a
    /// rising value explains a picture that is fine with sound that has holes in it.
    /// </summary>
    public long AudioDroppedBytes { get; set; }

    public long TotalBytes { get; set; }

    public DateTime? StartedUtc { get; set; }

    public long UptimeSeconds { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// The RTSP URL the paired receiver opens, credentials included.
    ///
    /// It is built per session: a fresh random path, a fresh credential and (usually) the same port.
    /// It is only ever handed out by this authenticated API, so a device that is not paired with this
    /// PC never sees it, and it stops working the moment the session stops. That is the whole reason
    /// the URL travels in this payload instead of being a constant the client could guess.
    /// </summary>
    public string? StreamUrl { get; set; }

    /// <summary>The transport the URL speaks. Named so a client never has to guess from the string.</summary>
    public string Transport { get; set; } = "rtsp";

    /// <summary>Relay path for this session (diagnostics; the URL already contains it).</summary>
    public string? RelayPath { get; set; }
}

/// <summary>Outcome of POST /mirror/stop.</summary>
public sealed class MirrorStopDto
{
    public bool Stopped { get; set; }

    public MirrorState State { get; set; } = MirrorState.Idle;
}

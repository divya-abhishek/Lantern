namespace Lantern.Media.Mirroring;

using Lantern.Core.Models;
using Lantern.Media.Transcoding;

/// <summary>
/// The screen mirroring ladder. Values are deliberately conservative: the receiving projector is a
/// 1 GB 1080p device, so stability beats resolution, and the defaults are 720p30 / Medium.
/// </summary>
public static class MirrorProfiles
{
    /// <summary>Targets offered by the UI, as (width, height). 4K is explicitly out of scope.</summary>
    public static readonly (int Width, int Height)[] Resolutions = [(1280, 720), (1920, 1080)];

    /// <summary>
    /// Frame rates offered. 30 stays the default - it is what the receiver is planned around and what
    /// every published figure was measured at. 60 is offered as well, and it is a setting rather than a
    /// claim because the three things that decide whether the pipeline can hold it are all in place:
    /// the encoder runs with no B-frames and no lookahead (see <see cref="MirrorCommandBuilder"/>), the
    /// keyframe interval stays half a second at either rate (<see cref="GopFor"/>), and the declared
    /// level is derived from the size *and* the rate (<see cref="EncoderProfiles.LevelFor"/>), so
    /// 1080p60 is declared as level 4.2 instead of being refused by the hardware encoder outright.
    /// </summary>
    public static readonly int[] FrameRates = [30, 60];

    /// <summary>Encoders in preference order: hardware first, software last.</summary>
    public static readonly string[] EncoderPriority =
    [
        "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264",
    ];

    /// <summary>Capture backends in preference order. ddagrab is the GPU Desktop Duplication path.</summary>
    public static readonly string[] CaptureBackendPriority = ["ddagrab", "gdigrab"];

    public static bool IsHardware(string encoder) =>
        encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
        || encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase)
        || encoder.Contains("amf", StringComparison.OrdinalIgnoreCase)
        || encoder.Contains("mf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Snaps arbitrary numbers onto the supported ladder (720p / 1080p).</summary>
    public static (int Width, int Height) NormalizeResolution(int width, int height) =>
        height >= 1080 ? (1920, 1080) : (1280, 720);

    /// <summary>
    /// Snaps a requested frame rate onto the ladder: 60 at or above 50, 30 below it. Everything else -
    /// a zero from an older client, a stray number from a hand-written request - lands on 30, which is
    /// the rate the receiver is planned around and the safe answer when the request makes no sense.
    /// </summary>
    public static int NormalizeFrameRate(int fps) => fps >= 50 ? 60 : 30;

    /// <summary>Quality ladder step from a client string ("low"/"medium"/"high").</summary>
    public static StreamQuality ParseQuality(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => StreamQuality.Low,
        "high" => StreamQuality.High,
        _ => StreamQuality.Medium,
    };

    /// <summary>
    /// Target bitrate for one ladder step. Real-time H.264 with no B-frames: 1.5 Mbps at 720p30 Low
    /// up to 12 Mbps at 1080p60 High, which is what a 5 GHz LAN carries without drama.
    /// </summary>
    public static int BitrateBps(int width, int height, int fps, StreamQuality quality)
    {
        var qualityFactor = quality switch
        {
            StreamQuality.Original or StreamQuality.High or StreamQuality.Auto => 1.25,
            StreamQuality.Low => 0.5,
            _ => 1.0,
        };

        var bitsPerFrame = height >= 1080 ? 200_000 : 100_000;

        return (int)(bitsPerFrame * fps * qualityFactor);
    }

    /// <summary>Half a second of frames: the longest a late receiver waits for a keyframe.</summary>
    public static int GopFor(int fps) => Math.Max(1, fps / 2);

    /// <summary>
    /// H.264 level that covers the target and that cheap Android decoders accept.
    ///
    /// Delegates to the transcoder's calculation so that mirroring and transcoding cannot disagree
    /// about a level again: 1280 × 720 at 60 fps used to be declared level 3.1, which covers half of
    /// that rate, and the encoder refuses a level that does not cover the stream.
    /// </summary>
    public static string LevelFor(int height, int fps) => EncoderProfiles.LevelFor(height, fps);

    public static string QualityLabel(StreamQuality quality) => quality switch
    {
        StreamQuality.High or StreamQuality.Original => "High",
        StreamQuality.Low => "Low",
        _ => "Medium",
    };

    /// <summary>Picks the first encoder from <paramref name="verified"/> that is on the priority list.</summary>
    public static string? PickEncoder(IReadOnlyCollection<string> verified)
    {
        foreach (var candidate in EncoderPriority)
        {
            if (verified.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }

        return verified.FirstOrDefault();
    }

    /// <summary>Picks the capture backend. ddagrab captures a single DXGI output, so it can only serve the
    /// whole desktop when the desktop is one display; with several monitors the virtual desktop
    /// needs gdigrab instead.
    /// </summary>
    public static string PickCaptureBackend(
        IReadOnlyCollection<string> backends, MirrorSource source, int displayCount)
    {
        var canUseDdagrab = source == MirrorSource.Display || displayCount <= 1;

        if (canUseDdagrab && backends.Contains("ddagrab", StringComparer.OrdinalIgnoreCase)) return "ddagrab";

        foreach (var candidate in CaptureBackendPriority)
        {
            if (backends.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }

        return backends.FirstOrDefault() ?? "gdigrab";
    }

    /// <summary>Resolves raw client input into a validated plan, snapped onto the ladder.</summary>
    public static MirrorCapturePlan Resolve(
        MirrorSource source,
        int displayIndex,
        int width,
        int height,
        int fps,
        StreamQuality quality,
        bool audio,
        string encoder,
        string captureBackend,
        int displayCount)
    {
        var (normalizedWidth, normalizedHeight) = NormalizeResolution(width, height);
        var normalizedFps = NormalizeFrameRate(fps);

        return new MirrorCapturePlan
        {
            Source = source,
            DisplayIndex = Math.Clamp(displayIndex, 0, Math.Max(0, displayCount - 1)),
            Width = normalizedWidth,
            Height = normalizedHeight,
            Fps = normalizedFps,
            Quality = quality,
            Encoder = encoder,
            CaptureBackend = captureBackend,
            Audio = audio,
            BitrateBps = BitrateBps(normalizedWidth, normalizedHeight, normalizedFps, quality),
            GopFrames = GopFor(normalizedFps),
        };
    }
}

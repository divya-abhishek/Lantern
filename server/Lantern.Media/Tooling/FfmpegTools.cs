namespace Lantern.Media.Tooling;

/// <summary>The ffmpeg/ffprobe pair the media layer is going to use.</summary>
public sealed class FfmpegTools
{
    public static readonly FfmpegTools Missing = new();

    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }

    /// <summary>First line of <c>ffmpeg -version</c>.</summary>
    public string? Version { get; init; }

    /// <summary>Hardware encoders this build actually supports (h264_nvenc, hevc_qsv, ...).</summary>
    public IReadOnlyList<string> HardwareEncoders { get; init; } = [];

    /// <summary>Set when the executables could not be located.</summary>
    public string? Error { get; init; }

    public bool Available => FfmpegPath is not null;
    public bool CanProbe => FfprobePath is not null;
    public bool CanTranscode => FfmpegPath is not null;

    public bool SupportsEncoder(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder)) return false;
        if (encoder.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
        if (encoder is "libx264" or "libx265") return Available;
        return HardwareEncoders.Contains(encoder, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the encoder that will be used for a transcode, honouring the user
    /// preference and falling back to the software encoder when unavailable.
    /// </summary>
    public string ResolveVideoEncoder(string preference)
    {
        if (!string.IsNullOrWhiteSpace(preference)
            && !preference.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && SupportsEncoder(preference))
        {
            return preference;
        }

        // Prefer hardware encoders on a media PC; fall back to software x264.
        string[] preferred = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
        foreach (var candidate in preferred)
        {
            if (SupportsEncoder(candidate)) return candidate;
        }
        return "libx264";
    }
}

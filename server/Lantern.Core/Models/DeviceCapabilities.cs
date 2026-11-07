using System.Text.Json.Serialization;

namespace Lantern.Core.Models;

/// <summary>
/// Codec/container capabilities of a playback device. The Android client detects
/// these at runtime via MediaCodecList and reports them to the server, so the
/// server can decide direct play vs. transcode without ever assuming support.
/// </summary>
public sealed class DeviceCapabilities
{
    public required string DeviceId { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? Manufacturer { get; set; }

    /// <summary>Platform name, e.g. "Android".</summary>
    public string? Platform { get; set; }

    /// <summary>e.g. "Android 11".</summary>
    public string? OsVersion { get; set; }

    public int? ApiLevel { get; set; }

    /// <summary>Maximum decoder width reported/assumed for hardware video decoding.</summary>
    public int? MaxWidth { get; set; }

    /// <summary>Maximum decoder height reported/assumed for hardware video decoding.</summary>
    public int? MaxHeight { get; set; }

    public bool Hdr10 { get; set; }
    public bool Hlg { get; set; }
    public bool DolbyVision { get; set; }
    public bool Hevc10Bit { get; set; }
    public bool Av1Hardware { get; set; }

    /// <summary>Measured/estimated practical streaming bitrate in Mbps (0/unknown = unconstrained).</summary>
    public double? MeasuredThroughputMbps { get; set; }

    /// <summary>Normalized video codec names the device can decode (see <see cref="CodecNames"/>).</summary>
    public List<string> VideoCodecs { get; set; } = [];

    /// <summary>Normalized audio codec names the device can decode.</summary>
    public List<string> AudioCodecs { get; set; } = [];

    /// <summary>Container formats the client can open (mp4, mkv, webm, ts ...).</summary>
    public List<string> Containers { get; set; } = [];

    public Dictionary<string, string> Extra { get; set; } = new();

    /// <summary>True when the device reported hardware/software support for the given video codec.</summary>
    public bool SupportsVideo(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) && VideoCodecs.Contains(CodecNames.Normalize(codec))
        && (CodecNames.Normalize(codec) != CodecNames.Hevc || (Extra.TryGetValue("hevcHardware", out var hardware) && hardware == "true"));

    /// <summary>True when the device reported support for the given audio codec.</summary>
    public bool SupportsAudio(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) && AudioCodecs.Contains(CodecNames.Normalize(codec));

    /// <summary>
    /// The largest frame the decoder for one specific codec accepts, or null when nothing is known.
    ///
    /// This exists because a single device-wide size is not enough to decide with. An Android TV box
    /// that decodes 4K HEVC through one decoder and 1080p H.264 through another is the normal case, and
    /// a device-wide maximum of 2160 would send a 4K H.264 file to a decoder that cannot take it -
    /// which is what "some 4K files fail outright" looks like. Clients report the per-codec sizes in
    /// <see cref="Extra"/> as "{codec}MaxWidth" / "{codec}MaxHeight" (strings, like every other value
    /// in that bag).
    /// </summary>
    public (int Width, int Height)? MaxSizeFor(string? codec)
    {
        var normalized = CodecNames.Normalize(codec);
        if (string.IsNullOrEmpty(normalized)) return null;

        var perCodec = PerCodecMaxSize(normalized);
        if (perCodec is not null) return perCodec;

        // Nothing was reported for *this* codec, so the remaining numbers describe other decoders -
        // and for H.264 that is not good enough, because H.264 is the codec every transcode is
        // encoded as. The device-wide maximum describes the box's best decoder (4K HEVC on a TV
        // stick) rather than its H.264 one, so trusting it asks ffmpeg for 4K H.264 that the H.264
        // decoder cannot decode and that NVENC will not even accept at 50 fps ("Invalid Level",
        // measured). The result was a transcode with zero segments and a client that reported "the
        // PC could not read that file". An unknown H.264 decoder is therefore treated as 1080p,
        // which is what every box of this class has been measured to accept.
        if (normalized == CodecNames.H264) return (1920, 1080);

        // The client reports limits per codec, so a codec it did not mention is not covered by the
        // device-wide number either. The smallest reported limit is the honest guess; the largest
        // would be the one that fails.
        var smallest = SmallestReportedSize();
        if (smallest is not null) return smallest;

        return MaxWidth is > 0 && MaxHeight is > 0 ? (MaxWidth.Value, MaxHeight.Value) : null;
    }

    /// <summary>The size this codec's decoder reported, exactly as sent, or null when it did not.</summary>
    public (int Width, int Height)? PerCodecMaxSize(string? codec)
    {
        var normalized = CodecNames.Normalize(codec);
        if (string.IsNullOrEmpty(normalized)) return null;

        var width = ReadInt($"{normalized}MaxWidth");
        var height = ReadInt($"{normalized}MaxHeight");

        return width is > 0 && height is > 0 ? (width.Value, height.Value) : null;
    }

    private (int Width, int Height)? SmallestReportedSize()
    {
        (int Width, int Height)? smallest = null;

        foreach (var pair in Extra)
        {
            if (!pair.Key.EndsWith("MaxHeight", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(pair.Value, out var height) || height <= 0) continue;

            var codec = pair.Key[..^"MaxHeight".Length];
            var width = ReadInt($"{codec}MaxWidth");
            if (width is null or <= 0) continue;

            if (smallest is null || height < smallest.Value.Height) smallest = (width.Value, height);
        }

        return smallest;
    }

    private int? ReadInt(string key) =>
        Extra.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;

    /// <summary>A conservative default used before a client reports its real capabilities.</summary>
    public static DeviceCapabilities Unknown(string deviceId = "unknown") => new()
    {
        DeviceId = deviceId,
        Name = "Unknown device",
        MaxWidth = 1920,
        MaxHeight = 1080,
        VideoCodecs = [CodecNames.H264, CodecNames.Mpeg4, CodecNames.Mpeg2],
        AudioCodecs = [CodecNames.Aac, CodecNames.Mp3],
        Containers = [CodecNames.Mp4, CodecNames.Ts],
    };
}

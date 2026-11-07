using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lantern.Media.Probing;

/// <summary>Raw shape of <c>ffprobe -print_format json -show_streams -show_format</c>.</summary>
internal sealed class FfprobeOutput
{
    [JsonPropertyName("streams")] public List<FfprobeStream>? Streams { get; set; }
    [JsonPropertyName("format")] public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
    [JsonPropertyName("codec_long_name")] public string? CodecLongName { get; set; }
    [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("pix_fmt")] public string? PixelFormat { get; set; }
    [JsonPropertyName("color_transfer")] public string? ColorTransfer { get; set; }
    [JsonPropertyName("color_primaries")] public string? ColorPrimaries { get; set; }
    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
    [JsonPropertyName("channels")] public int? Channels { get; set; }
    [JsonPropertyName("channel_layout")] public string? ChannelLayout { get; set; }
    [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonPropertyName("disposition")] public FfprobeDisposition? Disposition { get; set; }
    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }

    /// <summary>"30000/1001" style frame rate.</summary>
    [JsonPropertyName("avg_frame_rate")] public string? AverageFrameRate { get; set; }
    [JsonPropertyName("r_frame_rate")] public string? RealFrameRate { get; set; }

    [JsonPropertyName("side_data_list")] public List<FfprobeSideData>? SideData { get; set; }

    public string? Title => Tags is not null && Tags.TryGetValue("title", out var t) ? t : null;
    public string? Language => Tags is not null && Tags.TryGetValue("language", out var l) ? l : null;

    public double? FrameRate => ParseFraction(AverageFrameRate) ?? ParseFraction(RealFrameRate);

    public static double? ParseFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
        {
            return num / den;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : null;
    }
}

internal sealed class FfprobeDisposition
{
    [JsonPropertyName("default")] public int Default { get; set; }
    [JsonPropertyName("forced")] public int Forced { get; set; }
    [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; }
    [JsonPropertyName("comment")] public int Comment { get; set; }

    /// <summary>Cover art is flagged as an attached picture; it is not a real video stream.</summary>
    public bool IsCoverArt => AttachedPic == 1;
}

internal sealed class FfprobeSideData
{
    [JsonPropertyName("side_data_type")] public string? SideDataType { get; set; }
    [JsonPropertyName("dv_profile")] public int? DvProfile { get; set; }
    [JsonPropertyName("dv_bl_signal_compatibility_id")] public int? DvBlCompatibilityId { get; set; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("format_name")] public string? FormatName { get; set; }
    [JsonPropertyName("format_long_name")] public string? FormatLongName { get; set; }
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
    [JsonPropertyName("size")] public string? Size { get; set; }
    [JsonPropertyName("nb_streams")] public int? StreamCount { get; set; }
    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }

    public static double? ParseDuration(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
}

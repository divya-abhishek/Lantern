namespace Lantern.Core.Models;

/// <summary>
/// Normalizes FFprobe / MediaCodec codec names into the short names used by the
/// compatibility engine and the wire protocol (h264, hevc, eac3, ...).
/// </summary>
public static class CodecNames
{
    public const string H264 = "h264";
    public const string Hevc = "hevc";
    public const string Vp8 = "vp8";
    public const string Vp9 = "vp9";
    public const string Av1 = "av1";
    public const string Mpeg2 = "mpeg2";
    public const string Mpeg4 = "mpeg4";
    public const string Vc1 = "vc1";
    public const string Wmv3 = "wmv3";
    /// <summary>Microsoft's MPEG-4 variants (MP42/MP43): a different codec from MPEG-4 part 2.</summary>
    public const string MsMpeg4 = "msmpeg4";
    public const string Theora = "theora";
    public const string Mjpeg = "mjpeg";

    public const string Aac = "aac";
    public const string Ac3 = "ac3";
    public const string Eac3 = "eac3";
    public const string Mp3 = "mp3";
    public const string Mp2 = "mp2";
    public const string Opus = "opus";
    public const string Vorbis = "vorbis";
    public const string Flac = "flac";
    public const string Alac = "alac";
    public const string Dts = "dts";
    public const string DtsHd = "dtshd";
    public const string TrueHd = "truehd";
    public const string Pcm = "pcm";
    public const string Amr = "amr";
    public const string Wma = "wma";

    public const string Mp4 = "mp4";
    public const string Mov = "mov";
    public const string Mkv = "mkv";
    public const string Webm = "webm";
    public const string Avi = "avi";
    public const string Ts = "ts";
    public const string M2ts = "m2ts";
    public const string MpegPs = "mpg";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // video
        ["h264"] = H264, ["avc"] = H264, ["avc1"] = H264, ["x264"] = H264, ["h.264"] = H264, ["avc3"] = H264,
        ["hevc"] = Hevc, ["h265"] = Hevc, ["h.265"] = Hevc, ["x265"] = Hevc, ["hev1"] = Hevc, ["hvc1"] = Hevc,
        ["vp8"] = Vp8, ["vp9"] = Vp9, ["av01"] = Av1, ["av1"] = Av1,
        ["mpeg2video"] = Mpeg2, ["mpeg2"] = Mpeg2, ["mp2v"] = Mpeg2, ["mpeg-2"] = Mpeg2,
        ["mpeg4"] = Mpeg4, ["mp4v"] = Mpeg4, ["xvid"] = Mpeg4, ["divx"] = Mpeg4,
        // Microsoft's MPEG-4 v2/v3 are NOT MPEG-4 part 2: Android has no decoder for them, and
        // Media3's AVI extractor reads MP43 as "video/mp43", so they must keep their own name.
        ["msmpeg4v3"] = MsMpeg4, ["msmpeg4v2"] = MsMpeg4,
        ["vc1"] = Vc1, ["wmv3"] = Wmv3, ["wmav2"] = Wma, ["theora"] = Theora, ["mjpeg"] = Mjpeg,

        // audio
        ["aac"] = Aac, ["mp4a"] = Aac, ["aac_latm"] = Aac,
        ["ac3"] = Ac3, ["ac-3"] = Ac3, ["eac3"] = Eac3, ["ec-3"] = Eac3, ["eac-3"] = Eac3,
        ["mp3"] = Mp3, ["mp2"] = Mp2, ["opus"] = Opus, ["vorbis"] = Vorbis,
        ["flac"] = Flac, ["alac"] = Alac,
        ["dts"] = Dts, ["dca"] = Dts, ["dtshd"] = DtsHd, ["truehd"] = TrueHd, ["mlp"] = TrueHd,
        ["pcm_s16le"] = Pcm, ["pcm_s24le"] = Pcm, ["pcm_bluray"] = Pcm, ["pcm_dvd"] = Pcm,
        ["lpcm"] = Pcm, ["raw"] = Pcm, ["pcm"] = Pcm, ["amr_nb"] = Amr, ["amr_wb"] = Amr,
        ["wmav2"] = Wma,

        // containers
        ["mp4"] = Mp4, ["mov"] = Mp4, ["m4v"] = Mp4, ["3gp"] = Mp4, ["mkv"] = Mkv, ["matroska"] = Mkv,
        ["webm"] = Webm, ["avi"] = Avi, ["ts"] = Ts, ["mpegts"] = Ts, ["m2ts"] = Ts, ["mts"] = Ts,
        ["mpg"] = MpegPs, ["mpeg"] = MpegPs, ["vob"] = MpegPs,
    };

    /// <summary>Maps a raw codec/format name to its canonical short form (lower-cased raw name when unknown).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var key = raw.Trim();
        // Strip ffprobe decorations such as "hevc (Main 10)" or "eac3 (Dolby Digital Plus)".
        var space = key.IndexOf(' ');
        if (space > 0) key = key[..space];
        var paren = key.IndexOf('(');
        if (paren > 0) key = key[..paren];
        return Map.TryGetValue(key, out var canonical) ? canonical : key.ToLowerInvariant();
    }

    /// <summary>Audio codecs that are lossless/high bitrate and therefore heavy for HDMI/Bluetooth links.</summary>
    public static bool IsHeavyAudio(string? codec)
    {
        var c = Normalize(codec);
        return c is TrueHd or DtsHd or Dts or Pcm or Flac;
    }
}

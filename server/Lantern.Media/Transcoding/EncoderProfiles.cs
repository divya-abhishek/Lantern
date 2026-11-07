using System.Globalization;
using Lantern.Core.Models;

namespace Lantern.Media.Transcoding;
internal static class EncoderProfiles
{
    public static int QualityFor(StreamQuality quality) => quality switch
    {
        StreamQuality.Original => 18,
        StreamQuality.High => 20,
        StreamQuality.Medium => 23,
        StreamQuality.Low => 28,
        _ => 21, // Auto
    };

    public static bool IsHardware(string encoder) =>
        encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
        || encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase)
        || encoder.Contains("amf", StringComparison.OrdinalIgnoreCase)
        || encoder.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase);
    public static void AppendQuality(
        List<string> args, string encoder, StreamQuality quality, long? bitrateBps, int? sourceHeight)
    {
        var q = QualityFor(quality);

        if (bitrateBps is > 0)
        {
            var rate = bitrateBps.Value;
            args.Add("-b:v");
            args.Add(rate.ToString(CultureInfo.InvariantCulture));
            args.Add("-maxrate");
            args.Add(rate.ToString(CultureInfo.InvariantCulture));
            args.Add("-bufsize");
            args.Add((rate * 2).ToString(CultureInfo.InvariantCulture));
            if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-rc");
                args.Add("vbr");
            }
            else if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-maxrate");
                args.Add(rate.ToString(CultureInfo.InvariantCulture));
            }
            return;
        }

        if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-rc");
            args.Add("vbr");
            args.Add("-cq");
            args.Add(q.ToString(CultureInfo.InvariantCulture));
            args.Add("-b:v");
            args.Add("0");
            args.Add("-preset");
            args.Add(sourceHeight is >= 1800 ? "p4" : "p5");
        }
        else if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-global_quality");
            args.Add(q.ToString(CultureInfo.InvariantCulture));
            args.Add("-look_ahead");
            args.Add("0");
        }
        else if (encoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-rc");
            args.Add("cqp");
            args.Add("-qp_i");
            args.Add((q - 2).ToString(CultureInfo.InvariantCulture));
            args.Add("-qp_p");
            args.Add((q - 2).ToString(CultureInfo.InvariantCulture));
            args.Add("-quality");
            args.Add("balanced");
        }
        else
        {
            args.Add("-crf");
            args.Add(q.ToString(CultureInfo.InvariantCulture));
            args.Add("-preset");
            args.Add(sourceHeight is >= 1800 ? "faster" : "veryfast");
            args.Add("-sc_threshold");
            args.Add("0");
        }
    }
    public static void AppendGop(List<string> args, string encoder, double? frameRate, int segmentSeconds)
    {
        var fps = WholeNumberRate(frameRate);

        args.Add("-fps_mode");
        args.Add("cfr");
        args.Add("-r");
        args.Add(fps.ToString(CultureInfo.InvariantCulture));

        var gop = Math.Max(12, (int)Math.Round(fps * Math.Max(1, segmentSeconds)));
        args.Add("-g");
        args.Add(gop.ToString(CultureInfo.InvariantCulture));
        args.Add("-keyint_min");
        args.Add(gop.ToString(CultureInfo.InvariantCulture));
        args.Add("-force_key_frames");
        args.Add($"expr:gte(t,n_forced*{Math.Max(1, segmentSeconds)})");

        if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-forced-idr");
            args.Add("1");
        }
    }
    public static double WholeNumberRate(double? frameRate) =>
        frameRate is > 0 and < 121 ? Math.Round(frameRate.Value) : 30.0;
    public static void AppendProfile(List<string> args, int? height, double? frameRate, int? width = null)
    {
        args.Add("-profile:v");
        args.Add("high");
        args.Add("-level");
        args.Add(LevelFor(height, frameRate, width));
        args.Add("-pix_fmt");
        args.Add("yuv420p");
    }
    public static string LevelFor(int? height, double? frameRate, int? width = null)
    {
        var heightUsed = height is > 0 ? height.Value : 720;
        var widthUsed = width is > 0 ? width.Value : (int)Math.Round(heightUsed * 16.0 / 9.0);
        var fps = frameRate is > 0 and < 241 ? Math.Ceiling(frameRate.Value) : 30.0;

        var macroblocks = (long)Math.Ceiling(widthUsed / 16.0) * (long)Math.Ceiling(heightUsed / 16.0);
        var perSecond = macroblocks * fps;

        var start = heightUsed switch
        {
            >= 1800 => "5.1",
            >= 1200 => "5.0",
            >= 900 => "4.1",
            >= 620 => "3.1",
            _ => "4.0",
        };

        foreach (var level in Levels.SkipWhile(name => name != start))
        {
            if (MacroblocksCovered(level, macroblocks, perSecond)) return level;
        }

        return "5.2";
    }
    private static readonly string[] Levels = ["3.1", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2"];

    private static bool MacroblocksCovered(string level, long macroblocks, double perSecond)
    {
        var (maxPerSecond, maxFrame) = level switch
        {
            "3.1" => (108_000L, 3_600L),
            "4.0" or "4.1" => (245_760L, 8_192L),
            "4.2" => (522_240L, 8_704L),
            "5.0" => (589_824L, 22_080L),
            "5.1" => (983_040L, 36_864L),
            _ => (2_073_600L, 36_864L),
        };

        return perSecond <= maxPerSecond && macroblocks <= maxFrame;
    }
}

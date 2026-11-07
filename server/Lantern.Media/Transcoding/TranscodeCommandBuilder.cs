using System.Globalization;
using System.Text;
using Lantern.Core.Models;
using Lantern.Media.Tooling;

namespace Lantern.Media.Transcoding;
public static class TranscodeCommandBuilder
{
    public static string ResolveEncoder(TranscodeRequest request, FfmpegTools tools) =>
        request.CopyVideo ? "copy" : tools.ResolveVideoEncoder(request.EncoderPreference);
    public static List<string> BuildMp4(TranscodeRequest request, FfmpegTools tools)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-progress", "pipe:2",   // machine readable progress on stderr
            "-y",
        };
        if (request.StartMs > 0)
        {
            args.Add("-ss");
            args.Add(Seconds(request.StartMs));
        }

        args.Add("-i");
        args.Add(request.SourcePath);

        AppendStreamSelection(args, request, tools);
        AppendMp4Output(args, request);
        return args;
    }

    private static void AppendStreamSelection(List<string> args, TranscodeRequest request, FfmpegTools tools)
    {
        var plan = request.Plan;
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map_metadata");
        args.Add("0");

        if (!request.AudioOnly)
        {
            args.Add("-map");
            args.Add("0:v:0");
        }

        if (plan.AudioAction.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-an");
        }
        else
        {
            args.Add("-map");
            args.Add("0:a?");
        }

        if (!request.AudioOnly) AppendVideo(args, request, tools);
        AppendAudio(args, request);
    }

    private static void AppendVideo(List<string> args, TranscodeRequest request, FfmpegTools tools)
    {
        var plan = request.Plan;

        if (request.CopyVideo)
        {
            args.Add("-c:v");
            args.Add("copy");
            return;
        }

        var encoder = tools.ResolveVideoEncoder(request.EncoderPreference);
        args.Add("-c:v");
        args.Add(encoder);
        var scale = BuildScaleFilter(plan);
        if (scale is not null)
        {
            args.Add("-vf");
            args.Add(scale);
        }

        var targetHeight = plan.OutputHeight ?? 0;
        EncoderProfiles.AppendQuality(args, encoder, QualityFor(plan), plan.OutputVideoBitrateBps, targetHeight > 0 ? targetHeight : null);
        EncoderProfiles.AppendProfile(args, targetHeight > 0 ? targetHeight : null, request.SourceFrameRate,
            plan.OutputWidth > 0 ? plan.OutputWidth : null);
        EncoderProfiles.AppendGop(args, encoder, request.SourceFrameRate, request.SegmentSeconds);

        if (!EncoderProfiles.IsHardware(encoder))
        {
            args.Add("-threads");
            args.Add("0");
        }
    }

    private static void AppendAudio(List<string> args, TranscodeRequest request)
    {
        var plan = request.Plan;
        if (plan.AudioAction.Equals("none", StringComparison.OrdinalIgnoreCase)) return;

        // All tracks survive conversion, with language/title metadata. AAC-LC stereo
        // is deliberately uniform so selecting another track cannot break decoding.
        args.AddRange(["-c:a", "aac", "-profile:a", "aac_low", "-b:a", "192k", "-ac", "2", "-ar", "48000"]);
    }

    private static void AppendMp4Output(List<string> args, TranscodeRequest request)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        args.AddRange(["-max_muxing_queue_size", "2048", "-movflags", "+faststart", "-f", "mp4"]);
        args.Add(Path.Combine(request.OutputDirectory, "preparing.mp4"));
    }

    private static string? BuildScaleFilter(PlaybackPlan plan)
    {
        var height = plan.OutputHeight ?? 0;
        var width = plan.OutputWidth ?? 0;

        var parts = new List<string>();
        if (height > 0 && width > 0) parts.Add($"scale={width}:{height}");
        else if (height > 0) parts.Add($"scale=-2:{height}");
        else if (width > 0) parts.Add($"scale={width}:-2");
        parts.Add("format=yuv420p");
        return parts.Count == 0 ? null : string.Join(',', parts);
    }
    private static StreamQuality QualityFor(PlaybackPlan plan) => plan.OutputVideoBitrateBps switch
    {
        null => StreamQuality.Auto,
        >= 12_000_000 => StreamQuality.High,
        >= 6_000_000 => StreamQuality.Medium,
        _ => StreamQuality.Low,
    };
    public static List<string> BuildSubtitleExtract(
        string sourcePath, int streamIndex, string outputPath, string format = "webvtt")
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", sourcePath,
            "-map", $"0:{streamIndex}",
            "-c:s", format,
            "-f", format,
            outputPath,
        ];
    }
    public static List<string> BuildThumbnail(
        string sourcePath, long positionMs, string outputPath, int width = 480)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-ss", Seconds(positionMs),
            "-i", sourcePath,
            "-frames:v", "1",
            "-vf", $"scale={width}:-2:flags=bicubic",
            "-q:v", "3",
            "-f", "image2",
            outputPath,
        ];
    }
    public static string FormatArguments(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(arg.Contains(' ') || arg.Contains('"') ? $"\"{arg}\"" : arg);
        }
        return sb.ToString();
    }

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}

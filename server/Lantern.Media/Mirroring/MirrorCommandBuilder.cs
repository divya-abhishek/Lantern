namespace Lantern.Media.Mirroring;

using System.Globalization;
using Lantern.Core.Models;
public static class MirrorCommandBuilder
{
    public const int AudioSampleRate = 48_000;

    public const int AudioChannels = 2;

    public const int AudioBitrateBps = 128_000;
    // Anchor once, at acquisition (inside the lavfi source), then preserve capture PTS.
    // Re-stamping every frame at filter-processing time collapses queued frames into
    // bursts; the fps filter then emits duplicates while still reporting 60fps.
    public static string VideoClock(long captureEpochMicros) => captureEpochMicros > 0
        ? "setpts='PTS-STARTPTS+if(eq(N,0),st(0,(RTCTIME-" + captureEpochMicros.ToString(CultureInfo.InvariantCulture)
            + ")/1000000),ld(0))/TB'"
        : "setpts=PTS-STARTPTS";
    private static string AudioClock(int sampleRate) =>
        "asetpts=N/SR/TB";
    public static List<string> Build(
        MirrorCapturePlan plan, string publishUrl, long captureEpochMicros = 0)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning" };
        if (!plan.Audio) args.Add("-nostdin");
        args.Add("-copyts");

        AppendVideoInput(args, plan, captureEpochMicros);
        if (plan.Audio) AppendAudioInput(args, plan);

        AppendVideoFilters(args, plan, captureEpochMicros);
        args.Add("-map");
        args.Add("[v]");

        if (plan.Audio)
        {
            args.Add("-map");
            args.Add("[a]");
        }
        args.Add("-fps_mode");
        args.Add("passthrough");

        AppendVideoEncoder(args, plan);

        if (plan.Audio) AppendAudioEncoder(args);
        else AppendNoAudio(args);

        AppendRelayOutput(args, publishUrl);
        return args;
    }
    public static List<string> BuildEncoderProbe(MirrorCapturePlan plan)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi",
            "-i", $"color=c=black:s={plan.Width}x{plan.Height}:r={plan.Fps}",
            "-frames:v", "3",
        };

        AppendVideoEncoder(args, plan);
        AppendNoAudio(args);
        args.Add("-f");
        args.Add("null");
        args.Add("-");
        return args;
    }
    private static void AppendVideoInput(List<string> args, MirrorCapturePlan plan, long captureEpochMicros)
    {
        if (IsDdagrab(plan))
        {
            var index = plan.Source == MirrorSource.Display ? plan.DisplayIndex : 0;
            args.AddRange(["-analyzeduration", "1", "-probesize", "32"]);
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add($"ddagrab=output_idx={index}:framerate={plan.Fps}:dup_frames=1,{VideoClock(captureEpochMicros)}");
            return;
        }

        args.Add("-f");
        args.Add("gdigrab");
        args.Add("-framerate");
        args.Add(plan.Fps.ToString(CultureInfo.InvariantCulture));
        args.Add("-draw_mouse");
        args.Add("1");
        args.Add("-i");
        args.Add("desktop");
    }

    private static void AppendAudioInput(List<string> args, MirrorCapturePlan plan)
    {
        args.AddRange(["-analyzeduration", "1", "-probesize", "32", "-thread_queue_size", "8"]);
        args.Add("-f");
        args.Add("s16le");
        args.Add("-ar");
        args.Add(plan.AudioSampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-ac");
        args.Add(plan.AudioChannels.ToString(CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add("pipe:0");
    }

    private static void AppendVideoFilters(List<string> args, MirrorCapturePlan plan, long captureEpochMicros)
    {
        var scale = $"scale={plan.Width}:{plan.Height}:flags=bicubic:force_original_aspect_ratio=decrease," +
                    $"pad={plan.Width}:{plan.Height}:(ow-iw)/2:(oh-ih)/2";
        // gdigrab supplies Unix wall-clock PTS; only subtract the common audio origin.
        var clock = captureEpochMicros > 0
            ? "setpts=PTS-" + captureEpochMicros.ToString(CultureInfo.InvariantCulture) + "/(1000000*TB)"
            : "setpts=PTS-STARTPTS";
        var grid = "fps=" + plan.Fps.ToString(CultureInfo.InvariantCulture);
        var video = IsDdagrab(plan)
            ? $"[0:v]hwdownload,format=bgra,{grid},{scale},format=yuv420p[v]"
            : $"[0:v]{clock},{grid},{scale},format=yuv420p[v]";

        // Keep independent media in independent scheduler graphs. With both branches
        // in one graph, FFmpeg's best-input scheduling blocks desktop acquisition
        // behind coarse PCM packets, then fps fills the missing pictures with duplicates.
        // Synchronization still comes from their common capture origin and PCM clock.
        args.Add("-filter_complex");
        args.Add(video);
        if (plan.Audio)
        {
            args.Add("-filter_complex");
            args.Add($"[1:a]{AudioClock(plan.AudioSampleRate)}[a]");
        }
    }

    private static bool IsDdagrab(MirrorCapturePlan plan) =>
        plan.CaptureBackend.Equals("ddagrab", StringComparison.OrdinalIgnoreCase);
    private static void AppendVideoEncoder(List<string> args, MirrorCapturePlan plan)
    {
        var encoder = plan.Encoder;
        var bitrate = plan.BitrateBps.ToString(CultureInfo.InvariantCulture);
        var bufsize = Math.Max(plan.BitrateBps / 2, 250_000).ToString(CultureInfo.InvariantCulture);
        var gop = plan.GopFrames.ToString(CultureInfo.InvariantCulture);

        args.Add("-c:v");
        args.Add(encoder);

        args.Add("-b:v");
        args.Add(bitrate);
        args.Add("-maxrate");
        args.Add(bitrate);
        args.Add("-bufsize");
        args.Add(bufsize);

        args.Add("-g");
        args.Add(gop);
        args.Add("-keyint_min");
        args.Add(gop);

        args.Add("-bf");
        args.Add("0");

        args.Add("-profile:v");
        args.Add("high");
        args.Add("-level");
        args.Add(MirrorProfiles.LevelFor(plan.Height, plan.Fps));
        args.Add("-pix_fmt");
        args.Add("yuv420p");

        AppendEncoderTuning(args, encoder);
    }

    private static void AppendEncoderTuning(List<string> args, string encoder)
    {
        if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-preset");
            args.Add("p1");
            args.Add("-tune");
            args.Add("ull");
            args.Add("-rc");
            args.Add("cbr");
            args.Add("-rc-lookahead");
            args.Add("0");
            args.Add("-no-scenecut");
            args.Add("1");
            return;
        }

        if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-preset");
            args.Add("veryfast");
            args.Add("-rc");
            args.Add("cbr");
            args.Add("-async_depth");
            args.Add("1");
            return;
        }

        if (encoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-usage");
            args.Add("lowlatency");
            args.Add("-rc");
            args.Add("cbr");
            args.Add("-quality");
            args.Add("speed");
            return;
        }

        if (encoder.Contains("mf", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-rate_control");
            args.Add("cbr");
            args.Add("-scenario");
            args.Add("display_remoting");
            return;
        }
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-tune");
        args.Add("zerolatency");
        args.Add("-sc_threshold");
        args.Add("0");
    }

    private static void AppendAudioEncoder(List<string> args)
    {
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add(AudioBitrateBps.ToString(CultureInfo.InvariantCulture));
        args.Add("-ar");
        args.Add(AudioSampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-ac");
        args.Add(AudioChannels.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendNoAudio(List<string> args)
    {
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
    }
    private static void AppendRelayOutput(List<string> args, string publishUrl)
    {
        // Flush one AAC access unit per RTP packet. Media3 1.5.1's RtpAacReader
        // timestamps aggregated AUs using their count instead of 1024 samples,
        // causing audio discontinuities and video-clock jumps, especially in silence.
        args.Add("-max_delay");
        args.Add("0");
        args.Add("-flush_packets");
        args.Add("1");
        args.Add("-max_interleave_delta");
        args.Add("100000");
        args.Add("-nostats");
        args.Add("-progress");
        args.Add("pipe:1");

        args.Add("-f");
        args.Add("rtsp");
        args.Add("-rtsp_transport");
        args.Add("tcp");
        args.Add("-pkt_size");
        args.Add("1200");
        args.Add(publishUrl);
    }
    public static string Redact(string text) => System.Text.RegularExpressions.Regex.Replace(
        text, @"(rtsp[s]?://)[^\s/]*@", "$1[redacted]@", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string FormatArguments(IEnumerable<string> args)
    {
        var parts = new List<string>();
        foreach (var raw in args)
        {
            var arg = Redact(raw);
            parts.Add(arg.Contains(' ') ? $"\"{arg}\"" : arg);
        }
        return string.Join(' ', parts);
    }
}


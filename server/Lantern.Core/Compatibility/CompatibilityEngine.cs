namespace Lantern.Core.Compatibility;

using Lantern.Core.Models;
public sealed class CompatibilityEngine
{
    private static readonly Dictionary<StreamQuality, int> QualityHeight = new()
    {
        [StreamQuality.Low] = 480,
        [StreamQuality.Medium] = 720,
        [StreamQuality.High] = 1080,
    };
    private static readonly HashSet<string> DefaultContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        CodecNames.Mp4, CodecNames.Mkv, CodecNames.Webm, CodecNames.Ts,
    };
    private static readonly HashSet<string> UnsupportedContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        CodecNames.Mov, CodecNames.Avi, CodecNames.MpegPs, "flv", "wmv", "rm", "rmvb", "ogv", "3gp",
    };

    public PlaybackPlan Evaluate(
        MediaFileRecord file,
        DeviceCapabilities? device,
        CompatibilityOptions? options = null,
        StreamQuality requestedQuality = StreamQuality.Auto,
        bool forceTranscode = false,
        bool audioOnly = false)
    {
        options ??= new CompatibilityOptions();
        device ??= DeviceCapabilities.Unknown();
        var plan = new PlaybackPlan { AudioOnly = audioOnly };

        var container = CodecNames.Normalize(file.Container);
        var vCodec = CodecNames.Normalize(file.VideoCodec);
        var hasVideo = !audioOnly && !string.IsNullOrEmpty(vCodec);
        var isAvi = container == CodecNames.Avi
            || string.Equals(Path.GetExtension(file.Path), ".avi", StringComparison.OrdinalIgnoreCase);
        if (isAvi) container = CodecNames.Avi;

        plan.Container = container;
        plan.OutputVideoCodec = vCodec;
        var containerKnownBad = UnsupportedContainers.Contains(container)
            || (!string.IsNullOrEmpty(container)
                && device.Containers.Count > 0
                && !device.Containers.Contains(container)
                && !DefaultContainers.Contains(container));
        var videoReasons = new List<string>();
        var needVideoTranscode = false;
        var aviForcesAudioTranscode = false;

        if (hasVideo)
        {
            if (isAvi)
            {
                needVideoTranscode = true;
                videoReasons.Add($"AVI files are converted on the PC so they play on {DeviceName(device)}");
                aviForcesAudioTranscode = true;
            }

            if (!device.SupportsVideo(vCodec) || (vCodec == CodecNames.Av1 && !device.Av1Hardware))
            {
                needVideoTranscode = true;
                videoReasons.Add($"{vCodec.ToUpperInvariant()} video is not decodable by {DeviceName(device)}");
            }

            if (file.IsHdr && !(device.Hdr10 || device.Hlg))
            {
                needVideoTranscode = true;
                videoReasons.Add("HDR video but the device reports no HDR support (tone mapping required)");
            }

            if (!string.IsNullOrEmpty(file.DolbyVisionProfile) && !device.DolbyVision)
            {
                needVideoTranscode = true;
                videoReasons.Add($"Dolby Vision ({file.DolbyVisionProfile}) is not supported by the device");
            }

            if (vCodec == CodecNames.Hevc && !device.Hevc10Bit && Is10Bit(file.PixelFormat))
            {
                needVideoTranscode = true;
                videoReasons.Add("10-bit HEVC is not supported by the device decoder");
            }
            var limit = device.MaxSizeFor(vCodec);
            if (limit is { } size)
            {
                if (file.Width is > 0 && size.Width > 0 && file.Width > size.Width)
                {
                    needVideoTranscode = true;
                    videoReasons.Add(
                        $"source width {file.Width} exceeds the {vCodec.ToUpperInvariant()} decoder limit of {size.Width}");
                }

                if (file.Height is > 0 && size.Height > 0 && file.Height > size.Height)
                {
                    needVideoTranscode = true;
                    videoReasons.Add(
                        $"source height {file.Height} exceeds the {vCodec.ToUpperInvariant()} decoder limit of {size.Height}");
                }
            }
        }
        var audioTracks = file.AudioTracks.Count > 0 ? file.AudioTracks.ToList() : [];
        var supportedAudio = audioTracks.Where(t => device.SupportsAudio(t.Codec)).ToList();
        var audioReasons = new List<string>();
        var needAudioTranscode = false;

        if (audioTracks.Count == 0 && file.AudioCodec is not null)
        {
            if (!device.SupportsAudio(file.AudioCodec))
            {
                needAudioTranscode = true;
                audioReasons.Add($"{file.AudioCodec.ToUpperInvariant()} audio is not supported by the device");
            }
        }
        else if (audioTracks.Count > 0 && supportedAudio.Count == 0)
        {
            needAudioTranscode = true;
            var list = string.Join(", ", audioTracks.Select(t => (t.Codec ?? "?").ToUpperInvariant()).Distinct());
            audioReasons.Add($"none of the audio tracks ({list}) are decodable by {DeviceName(device)}");
        }
        else if (supportedAudio.Count > 0)
        {
            var chosen = supportedAudio.FirstOrDefault(t => t.IsDefault) ?? supportedAudio[0];
            plan.SelectedAudioStreamIndex = chosen.Index;
        }

        if (aviForcesAudioTranscode && !needAudioTranscode)
        {
            needAudioTranscode = true;
            audioReasons.Add("AVI audio is converted to AAC so the converted file plays anywhere");
        }
        var quality = requestedQuality != StreamQuality.Auto ? requestedQuality : options.MaxQuality;
        if (quality is not (StreamQuality.Auto or StreamQuality.Original)
            && QualityHeight.TryGetValue(quality, out var qh)
            && file.Height is > 0 && file.Height > qh)
        {
            needVideoTranscode = true;
            videoReasons.Add($"quality requested is {qh}p (source is {file.Height}p)");
        }

        if (options.MaxHeightCap > 0 && file.Height is > 0 && file.Height > options.MaxHeightCap)
        {
            needVideoTranscode = true;
            videoReasons.Add($"server height cap is {options.MaxHeightCap}p");
        }

        if (options.MaxBitrateMbps > 0 && file.BitrateBps is > 0
            && file.BitrateBps > (long)(options.MaxBitrateMbps * 1_000_000))
        {
            needVideoTranscode = true;
            videoReasons.Add($"source bitrate {file.BitrateBps / 1_000_000.0:0.#} Mbps exceeds the {options.MaxBitrateMbps:0.#} Mbps cap");
        }

        if (forceTranscode)
        {
            needVideoTranscode = true;
            videoReasons.Add("the client requested transcoding");
        }

        if (needVideoTranscode)
        {
            plan.Mode = PlaybackMode.Transcode;
            plan.Decision = needAudioTranscode ? StreamDecision.TranscodeBoth : StreamDecision.TranscodeVideo;
            plan.VideoAction = "transcode";
            plan.Container = "mp4";
            plan.OutputVideoCodec = CodecNames.H264;
            plan.SupportsSeek = true;
            ApplyTargetVideoSize(plan, file, device, options, quality, audioOnly);
        }
        else if (containerKnownBad && options.AllowRemux)
        {
            plan.Mode = PlaybackMode.Remux;
            plan.Container = "mp4";
            plan.Notes.Add($"container '{file.Container}' is repackaged to MP4 for streaming");
        }

        if (needAudioTranscode)
        {
            if (plan.Mode == PlaybackMode.DirectPlay)
            {
                plan.Mode = options.AllowRemux ? PlaybackMode.Remux : PlaybackMode.Transcode;
            }
            plan.AudioAction = "transcode";
            plan.OutputAudioCodec = CodecNames.Aac;
            plan.OutputAudioBitrateKbps = 256;
            plan.SelectedAudioStreamIndex = audioTracks.FirstOrDefault()?.Index;
            plan.Decision = plan.VideoAction == "transcode" ? StreamDecision.TranscodeBoth : StreamDecision.TranscodeAudio;
        }
        else if (audioTracks.Count == 0 && file.AudioCodec is null)
        {
            plan.AudioAction = "none";
            plan.OutputAudioCodec = null;
        }
        else
        {
            plan.OutputAudioCodec = plan.SelectedAudioStreamIndex is { } idx
                ? audioTracks.FirstOrDefault(t => t.Index == idx)?.Codec ?? file.AudioCodec
                : file.AudioCodec;
        }

        if (plan.RequiresSession && plan.AudioAction != "none") {
            plan.AudioAction = "transcode";
            plan.OutputAudioCodec = CodecNames.Aac;
            plan.OutputAudioBitrateKbps = 192;
        }
        plan.Reason = BuildReason(plan, file, device, needVideoTranscode, needAudioTranscode, videoReasons, audioReasons);

        if (!hasVideo && !audioOnly)
        {
            plan.Notes.Add("the file has no video stream; audio only");
        }

        return plan;
    }
    private static void ApplyTargetVideoSize(
        PlaybackPlan plan, MediaFileRecord file, DeviceCapabilities device,
        CompatibilityOptions options, StreamQuality quality, bool audioOnly)
    {
        var targetHeight = file.Height is > 0 ? file.Height.Value : 1080;
        // Preparation is the compatibility fallback. Keep its default at 1080p;
        // direct play still preserves supported 4K originals without conversion.
        if (quality != StreamQuality.Original) targetHeight = Math.Min(targetHeight, 1080);

        if (quality is not (StreamQuality.Auto or StreamQuality.Original)
            && QualityHeight.TryGetValue(quality, out var qh))
        {
            targetHeight = Math.Min(targetHeight, qh);
        }
        var outputLimit = device.MaxSizeFor(CodecNames.H264);
        if (outputLimit is { } size && size.Height > 0)
        {
            targetHeight = Math.Min(targetHeight, size.Height);
        }
        else if (device.MaxHeight is > 0)
        {
            targetHeight = Math.Min(targetHeight, device.MaxHeight.Value);
        }

        if (options.MaxHeightCap > 0)
        {
            targetHeight = Math.Min(targetHeight, options.MaxHeightCap);
        }

        if (audioOnly) targetHeight = Math.Min(targetHeight, 2160);
        if (targetHeight <= 0) targetHeight = 1080;

        var targetWidth = file.Width is > 0 && file.Height is > 0
            ? (int)Math.Round(file.Width.Value * (double)targetHeight / file.Height.Value)
            : (int)Math.Round(targetHeight * 16.0 / 9.0);

        var maxWidth = outputLimit?.Width ?? 1920;
        if (targetWidth > maxWidth)
        {
            targetHeight = (int)(targetHeight * (double)maxWidth / targetWidth);
            targetWidth = maxWidth;
        }
        plan.OutputHeight = targetHeight - (targetHeight % 2);
        plan.OutputWidth = targetWidth - (targetWidth % 2);
        plan.OutputVideoBitrateBps = options.MaxBitrateMbps > 0
            ? (long)(options.MaxBitrateMbps * 1_000_000 * 0.95)
            : null;
    }

    private static string BuildReason(
        PlaybackPlan plan,
        MediaFileRecord file,
        DeviceCapabilities device,
        bool needVideoTranscode,
        bool needAudioTranscode,
        List<string> videoReasons,
        List<string> audioReasons)
    {
        var name = DeviceName(device);

        if (plan.Mode == PlaybackMode.DirectPlay)
        {
            var parts = new List<string>();
            if (file.Width is > 0 && file.Height is > 0) parts.Add($"{file.Width}×{file.Height}");
            if (!string.IsNullOrEmpty(plan.OutputVideoCodec)) parts.Add(plan.OutputVideoCodec.ToUpperInvariant());
            if (!string.IsNullOrEmpty(plan.OutputAudioCodec)) parts.Add(plan.OutputAudioCodec.ToUpperInvariant());
            var what = parts.Count > 0 ? string.Join(' ', parts) : "this file";
            return $"Direct Play: {what} is fully supported by {name}. Streaming the original file at full quality.";
        }

        if (plan.Mode == PlaybackMode.Remux)
        {
            var why = audioReasons.FirstOrDefault() ?? videoReasons.FirstOrDefault()
                ?? plan.Notes.FirstOrDefault() ?? "stream repackaging required";
            return $"Remux: {why}. Video is copied losslessly; audio is converted to AAC.";
        }

        var changes = new List<string>();
        if (needVideoTranscode)
        {
            var src = (file.VideoCodec ?? "unknown").ToUpperInvariant();
            var target = plan.OutputWidth is > 0 && plan.OutputHeight is > 0
                ? $"{plan.OutputWidth}×{plan.OutputHeight} H.264"
                : "H.264";
            changes.Add($"{src} video is being converted to {target}");
        }
        if (needAudioTranscode)
        {
            var src = file.AudioTracks.FirstOrDefault()?.Codec?.ToUpperInvariant()
                ?? (file.AudioCodec ?? "unknown").ToUpperInvariant();
            changes.Add($"{src} audio is being converted to AAC 256 kbps");
        }
        if (changes.Count == 0) changes.Add("transcoding");

        var hints = string.Join("; ", videoReasons.Concat(audioReasons).Distinct());
        var sentence = $"Transcoding: {string.Join(" and ", changes)}.";
        return string.IsNullOrWhiteSpace(hints) ? sentence : $"{sentence} {Capitalize(hints)}.";
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string DeviceName(DeviceCapabilities device) =>
        string.IsNullOrWhiteSpace(device.Name) ? "this device" : device.Name!;

    private static bool Is10Bit(string? pixelFormat) =>
        pixelFormat is not null
        && (pixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.Contains("12", StringComparison.OrdinalIgnoreCase));
}


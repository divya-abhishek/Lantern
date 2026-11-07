using System.Globalization;
using System.Text.Json;
using Lantern.Core.Models;
using Lantern.Media.Processes;
using Lantern.Media.Tooling;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Probing;

/// <summary>Thin wrapper around <c>ffprobe -print_format json</c>.</summary>
public sealed class FfprobeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private readonly FfmpegLocator _locator;
    private readonly ILogger<FfprobeRunner> _logger;

    public FfprobeRunner(FfmpegLocator locator, ILogger<FfprobeRunner> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>Reads all stream/format information of a file. Never throws for media errors.</summary>
    public async Task<MediaProbeResult> ProbeAsync(
        string path, LanternSettings settings, CancellationToken ct = default)
    {
        var tools = await _locator.ResolveAsync(settings, false, ct).ConfigureAwait(false);
        var ffprobe = tools.FfprobePath;
        if (ffprobe is null)
        {
            return new MediaProbeResult { Error = "ffprobe not found" };
        }

        var fileInfo = new FileInfo(path);
        var result = await ProcessRunner.RunAsync(
            ffprobe,
            [
                "-hide_banner", "-loglevel", "error",
                "-print_format", "json",
                "-show_format", "-show_streams",
                "-analyzeduration", "20M",
                "-probesize", "50M",
                path,
            ],
            TimeSpan.FromMinutes(2),
            _logger,
            ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogWarning("ffprobe failed for {Path}: {Error}", path, Truncate(result.StandardError));
            return new MediaProbeResult
            {
                Error = Truncate(result.StandardError),
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            };
        }

        try
        {
            var raw = JsonSerializer.Deserialize<FfprobeOutput>(result.StandardOutput, JsonOptions);
            return Map(raw, fileInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ffprobe output for {Path}", path);
            return new MediaProbeResult { Error = "invalid ffprobe output" };
        }
    }

    private static MediaProbeResult Map(FfprobeOutput? raw, FileInfo fileInfo)
    {
        var result = new MediaProbeResult
        {
            SizeBytes = FfprobeFormat.ParseLong(raw?.Format?.Size) ?? (fileInfo.Exists ? fileInfo.Length : 0),
        };

        if (raw?.Format is not null)
        {
            result.Container = CodecNames.Normalize(raw.Format.FormatName?.Split(',')[0]);
            var seconds = FfprobeFormat.ParseDuration(raw.Format.Duration);
            result.BitrateBps = FfprobeFormat.ParseLong(raw.Format.BitRate);
            if (seconds is > 0)
            {
                result.DurationMs = (long)Math.Round(seconds.Value * 1000);
            }
        }

        var streams = raw?.Streams ?? [];
        foreach (var stream in streams)
        {
            switch (stream.CodecType)
            {
                case "video" when stream.Disposition?.IsCoverArt != true:
                    MapVideo(result, stream);
                    break;
                case "audio":
                    result.AudioTracks.Add(MapAudio(stream));
                    break;
                case "subtitle":
                    result.SubtitleTracks.Add(MapSubtitle(stream));
                    break;
            }
        }

        result.AudioCodec = (result.AudioTracks.FirstOrDefault(t => t.IsDefault)
            ?? result.AudioTracks.FirstOrDefault())?.Codec;

        if (!result.HasVideoStream && result.AudioTracks.Count > 0)
        {
            result.BitrateBps ??= MediaProbeResult.SumAudioBitrate(streams);
        }

        if (result.DurationMs == 0)
        {
            var streamSeconds = streams
                .Select(s => FfprobeFormat.ParseDuration(s.Duration))
                .Where(d => d is > 0)
                .DefaultIfEmpty(0)
                .Max();
            result.DurationMs = (long)Math.Round(streamSeconds.GetValueOrDefault() * 1000);
        }

        return result;
    }

    private static void MapVideo(MediaProbeResult result, FfprobeStream stream)
    {
        result.HasVideoStream = true;
        result.VideoCodec = CodecNames.Normalize(stream.CodecName);
        result.Width = stream.Width;
        result.Height = stream.Height;
        result.FrameRate = stream.FrameRate;
        result.PixelFormat = stream.PixelFormat;
        result.BitrateBps ??= FfprobeFormat.ParseLong(stream.BitRate);

        // HDR10 / HLG are indicated by the transfer characteristics; Dolby Vision by side data.
        var transfer = stream.ColorTransfer?.ToLowerInvariant();
        result.IsHdr = transfer is "smpte2084" or "arib-std-b67"
            || (stream.ColorPrimaries?.Equals("bt2020", StringComparison.OrdinalIgnoreCase) == true
                && transfer is "smpte2084" or "arib-std-b67");

        foreach (var side in stream.SideData ?? [])
        {
            if (side.DvProfile is { } dvProfile && dvProfile > 0)
            {
                result.IsHdr = true;
                result.DolbyVisionProfile = $"dvhe.{dvProfile:00}";
            }
        }

        if (!result.IsHdr && stream.Profile?.Contains("Main 10", StringComparison.OrdinalIgnoreCase) == true)
        {
            // HEVC Main 10 is usually HDR ready; keep it flagged so the client can decide.
            result.IsHdr = stream.PixelFormat is "yuv420p10le" or "yuv420p12le";
        }
    }

    private static MediaTrackInfo MapAudio(FfprobeStream stream) => new()
    {
        Index = stream.Index,
        Codec = CodecNames.Normalize(stream.CodecName),
        Profile = stream.Profile,
        Language = stream.Language,
        Title = stream.Title,
        Channels = stream.Channels,
        ChannelLayout = stream.ChannelLayout,
        SampleRate = int.TryParse(stream.SampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sr) ? sr : null,
        BitrateBps = FfprobeFormat.ParseLong(stream.BitRate),
        IsDefault = stream.Disposition?.Default == 1,
        IsForced = stream.Disposition?.Forced == 1,
    };

    private static SubtitleTrackInfo MapSubtitle(FfprobeStream stream) => new()
    {
        Id = $"e:{stream.Index}",
        Codec = CodecNames.Normalize(stream.CodecName),
        Language = stream.Language,
        Title = stream.Title,
        IsDefault = stream.Disposition?.Default == 1,
        IsForced = stream.Disposition?.Forced == 1,
        IsExternal = false,
        StreamIndex = stream.Index,
    };


    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown error";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? text;
        return line.Length > 300 ? line[..300] : line;
    }
}

using System.Text;
using Lantern.Core.Models;
using Lantern.Media.Processes;
using Lantern.Media.Tooling;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// Turns a subtitle track into WebVTT for Media3. Text formats are converted locally
/// (fast, no process); ASS/SSA and embedded tracks are piped through ffmpeg.
/// </summary>
public sealed class SubtitleConverter
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<SubtitleConverter> _logger;

    public SubtitleConverter(FfmpegLocator locator, ILogger<SubtitleConverter> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>WebVTT text, or null when the track cannot be converted.</summary>
    public async Task<string?> GetWebVttAsync(
        MediaFileRecord file, SubtitleTrackInfo track, LanternSettings settings, CancellationToken ct = default)
    {
        var codec = CodecNames.Normalize(track.Codec);

        if (track.IsExternal && !string.IsNullOrWhiteSpace(track.ExternalPath) && File.Exists(track.ExternalPath))
        {
            if (codec is "webvtt" or "vtt" || track.ExternalPath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadVttAsync(track.ExternalPath, ct).ConfigureAwait(false);
            }

            if (codec is "subrip" or "srt" or "")
            {
                var text = await File.ReadAllTextAsync(track.ExternalPath, ct).ConfigureAwait(false);
                return SrtToVtt(text);
            }
        }

        if (track.StreamIndex is { } index)
        {
            return await TranscodeAsync(file.Path, $"0:{index}", settings, ct).ConfigureAwait(false);
        }

        if (track.IsExternal && !string.IsNullOrWhiteSpace(track.ExternalPath))
        {
            return await TranscodeAsync(track.ExternalPath, null, settings, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>ffmpeg "-f webvtt -" pass; <paramref name="map"/> is an ffmpeg stream specifier.</summary>
    private async Task<string?> TranscodeAsync(
        string sourcePath, string? map, LanternSettings settings, CancellationToken ct)
    {
        if (!File.Exists(sourcePath)) return null;

        var tools = await _locator.ResolveAsync(settings, false, ct).ConfigureAwait(false);
        if (tools.FfmpegPath is null) return null;

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", sourcePath };
        if (map is not null)
        {
            args.Add("-map");
            args.Add(map);
        }

        args.AddRange(["-f", "webvtt", "-"]);
        var result = await ProcessRunner
            .RunAsync(tools.FfmpegPath, args, TimeSpan.FromMinutes(2), _logger, ct)
            .ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogWarning(
                "Subtitle conversion failed for {Path} ({Codec}): {Error}",
                sourcePath, map ?? "all", result.StandardError.Trim());
            return null;
        }

        return result.StandardOutput.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
            ? result.StandardOutput
            : $"WEBVTT\n\n{result.StandardOutput}";
    }

    private static async Task<string> ReadVttAsync(string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return text.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ? text : $"WEBVTT\n\n{text}";
    }

    /// <summary>SRT and WebVTT differ only by the header and the decimal comma.</summary>
    public static string SrtToVtt(string srt)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var raw in srt.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("-->")) line = line.Replace(',', '.');
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }
}

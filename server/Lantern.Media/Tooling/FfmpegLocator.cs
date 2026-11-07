using Lantern.Core.Models;
using Lantern.Media.Processes;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Tooling;

/// <summary>
/// Finds ffmpeg/ffprobe on the machine (settings override, PATH, common install
/// folders) and caches the detected features.
/// </summary>
public sealed class FfmpegLocator
{
    private readonly ILogger<FfmpegLocator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FfmpegTools? _cached;
    private string? _cacheKey;

    public FfmpegLocator(ILogger<FfmpegLocator> logger) => _logger = logger;

    /// <summary>Returns the cached tools for the given settings, probing them once.</summary>
    public async Task<FfmpegTools> ResolveAsync(
        LanternSettings settings, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = $"{settings.FfmpegPath}|{settings.FfprobePath}";
        if (!forceRefresh && _cached is not null && _cacheKey == key)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is not null && _cacheKey == key)
            {
                return _cached;
            }

            _cached = await DetectAsync(settings, ct).ConfigureAwait(false);
            _cacheKey = key;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FfmpegTools> DetectAsync(LanternSettings settings, CancellationToken ct)
    {
        var ffmpeg = FindExecutable(settings.FfmpegPath, "ffmpeg");
        var ffprobe = FindExecutable(settings.FfprobePath, "ffprobe")
            ?? (ffmpeg is null ? null : FindExecutable(Path.GetDirectoryName(ffmpeg), "ffprobe"));

        if (ffmpeg is null)
        {
            _logger.LogWarning("ffmpeg was not found. Install it (winget install Gyan.FFmpeg) or set the path in settings.");
            return new FfmpegTools { Error = "ffmpeg not found" };
        }

        var version = await GetVersionAsync(ffmpeg, ct).ConfigureAwait(false);
        var encoders = await GetHardwareEncodersAsync(ffmpeg, ct).ConfigureAwait(false);
        _logger.LogInformation("Using ffmpeg at {Path} ({Version}); hardware encoders: {Encoders}",
            ffmpeg, version ?? "unknown", encoders.Count == 0 ? "none" : string.Join(", ", encoders));

        return new FfmpegTools
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ffprobe,
            Version = version,
            HardwareEncoders = encoders,
        };
    }

    private async Task<string?> GetVersionAsync(string ffmpeg, CancellationToken ct)
    {
        var result = await ProcessRunner
            .RunAsync(ffmpeg, ["-hide_banner", "-version"], TimeSpan.FromSeconds(15), _logger, ct)
            .ConfigureAwait(false);
        if (!result.Success) return null;
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private async Task<List<string>> GetHardwareEncodersAsync(string ffmpeg, CancellationToken ct)
    {
        var result = await ProcessRunner
            .RunAsync(ffmpeg, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), _logger, ct)
            .ConfigureAwait(false);
        if (!result.Success) return [];

        string[] wanted =
        [
            "h264_nvenc", "hevc_nvenc", "av1_nvenc",
            "h264_qsv", "hevc_qsv", "av1_qsv",
            "h264_amf", "hevc_amf", "av1_amf",
        ];

        var found = new List<string>();
        foreach (var line in result.Combined.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length < 8 || trimmed[0] != 'V') continue;
            var name = trimmed[6..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (name is not null && wanted.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(name);
            }
        }
        return found;
    }


    /// <summary>Accepts either a folder or a full path to the executable.</summary>
    public static string? FindExecutable(string? configured, string executableName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = configured.Trim().Trim('"');
            if (Directory.Exists(candidate))
            {
                var inDir = DirectLookup(candidate, executableName);
                if (inDir is not null) return inDir;
            }
            else if (File.Exists(candidate))
            {
                var name = Path.GetFileNameWithoutExtension(candidate);
                if (name.Equals(executableName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
                var sibling = DirectLookup(Path.GetDirectoryName(candidate) ?? string.Empty, executableName);
                if (sibling is not null) return sibling;
            }
        }

        return DirectLookup(Path.Combine(AppContext.BaseDirectory, "ffmpeg"), executableName)
            ?? DirectLookup(AppContext.BaseDirectory, executableName)
            ?? ResolveFromPath(executableName) ?? ResolveFromCommonLocations(executableName);
    }

    private static string? DirectLookup(string directory, string executableName)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var ext in new[] { ".exe", string.Empty })
        {
            var path = Path.Combine(directory, executableName + ext);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    private static string? ResolveFromPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var found = DirectLookup(dir.Trim(), executableName);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? ResolveFromCommonLocations(string executableName)
    {
        var roots = new List<string>
        {
            AppContext.BaseDirectory,
            @"C:\ffmpeg\bin",
            @"C:\Program Files\ffmpeg\bin",
            @"C:\Program Files (x86)\ffmpeg\bin",
            @"C:\tools\ffmpeg\bin",
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            roots.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
            roots.Add(Path.Combine(localAppData, "Programs", "ffmpeg", "bin"));
        }

        foreach (var root in roots)
        {
            var found = DirectLookup(root, executableName);
            if (found is not null) return found;
        }
        return null;
    }
}

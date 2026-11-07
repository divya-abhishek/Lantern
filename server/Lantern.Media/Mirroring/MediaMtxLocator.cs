namespace Lantern.Media.Mirroring;

using System.Diagnostics;
using Lantern.Media.Processes;
using Microsoft.Extensions.Logging;

/// <summary>
/// Finds the MediaMTX relay on this machine.
///
/// The relay is an implementation component of Lantern, not an application the user runs: it is
/// shipped (or dropped) next to the server, so the search order starts where the server itself lives
/// and only then looks at the machine. The same shape as <see cref="Tooling.FfmpegLocator"/> on
/// purpose - one place to point a tool path in, one place that reports why a tool is missing.
/// </summary>
public static class MediaMtxLocator
{
    /// <summary>File name with and without the extension, because Windows resolves both.</summary>
    private const string ExecutableName = "mediamtx";

    /// <summary>The path as configured, or the first candidate that exists, or null.</summary>
    public static string? Find(string? configured) => Find(configured, out _);

    /// <summary>
    /// Same, but also says why nothing was found so the caller can put it in front of a user.
    /// </summary>
    public static string? Find(string? configured, out string reason)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = configured.Trim().Trim('"');

            if (Directory.Exists(candidate))
            {
                var inDirectory = Lookup(candidate);
                if (inDirectory is not null)
                {
                    reason = string.Empty;
                    return inDirectory;
                }
            }
            else if (File.Exists(candidate))
            {
                reason = string.Empty;
                return Path.GetFullPath(candidate);
            }

            // A configured path that does not resolve is reported rather than silently ignored:
            // falling back to some other copy would hide the mistake the setting is trying to fix.
            reason = $"The configured relay path does not contain {ExecutableName}.exe: {candidate}";
            return null;
        }

        foreach (var candidate in Candidates())
        {
            if (candidate is null) continue;

            var found = Lookup(candidate);
            if (found is not null)
            {
                reason = string.Empty;
                return found;
            }
        }

        reason =
            $"The {ExecutableName} relay was not found. Put {ExecutableName}.exe next to the server " +
            "(the published layout has it in a 'mediamtx' folder), or point Lantern at it in Settings.";
        return null;
    }

    /// <summary>Directories worth looking in, nearest first.</summary>
    private static IEnumerable<string?> Candidates()
    {
        // Where the server itself runs from: the published layout copies the relay into
        // "mediamtx" beside the server, exactly as it does with ffmpeg's folder layout.
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "mediamtx");

        // A development run: walk up from bin/Debug/net9.0 to the repository root and use
        // tools/mediamtx, which is where the fetch script puts it.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++)
        {
            yield return Path.Combine(directory.FullName, "tools", "mediamtx");
            yield return Path.Combine(directory.FullName, "mediamtx");
            directory = directory.Parent;
        }

        foreach (var directoryOnPath in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directoryOnPath.Trim();
        }
    }

    private static string? Lookup(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        foreach (var extension in new[] { ".exe", string.Empty })
        {
            var path = Path.Combine(directory, ExecutableName + extension);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }

        return null;
    }

    /// <summary>
    /// First line of <c>mediamtx --version</c>, or null when the executable cannot be run.
    ///
    /// Asked for the same reason the ffmpeg version is asked for: the report says which build is
    /// carrying the picture, because "the relay" is not a version.
    /// </summary>
    public static async Task<string?> VersionAsync(
        string executable, ILogger logger, CancellationToken ct = default)
    {
        var result = await ProcessRunner
            .RunAsync(executable, ["--version"], TimeSpan.FromSeconds(10), logger, ct)
            .ConfigureAwait(false);

        if (!result.Success) return null;

        return result.Combined
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    /// <summary>The running relay's process name, used by the diagnostics to spot orphans.</summary>
    public static string ProcessName => ExecutableName;

    /// <summary>True when a relay process is running outside this server (an orphan from a crash).</summary>
    public static int RunningProcessCount()
    {
        try
        {
            return Process.GetProcessesByName(ExecutableName).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}

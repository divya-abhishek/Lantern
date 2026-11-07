using Lantern.Core.Models;
using Lantern.Core.Parsing;

namespace Lantern.Media.Scanning;

/// <summary>Finds the subtitle files that sit next to a movie (movie.srt, movie.eng.srt, ...).</summary>
internal static class SubtitleSidecars
{
    private static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt", ".sub"];

    /// <summary>Preferred language order when several sidecar files exist.</summary>
    private static readonly string[] PreferredLanguages = ["eng", "en", "und", ""];

    public static List<SubtitleTrackInfo> Find(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var tracks = new List<SubtitleTrackInfo>();

        foreach (var candidate in EnumerateCandidates(directory, baseName))
        {
            var fileName = Path.GetFileName(candidate);
            var language = ExtractLanguage(fileName, baseName);
            tracks.Add(new SubtitleTrackInfo
            {
                Id = $"f:{fileName}",
                Codec = CodecNames.Normalize(Path.GetExtension(candidate).TrimStart('.')),
                Language = language,
                IsExternal = true,
                ExternalPath = candidate,
                IsForced = fileName.Contains("forced", StringComparison.OrdinalIgnoreCase),
            });
        }

        return tracks
            .OrderBy(t => LanguageRank(t.Language))
            .ThenBy(t => t.IsForced)
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateCandidates(string directory, string baseName)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder not readable: silently skip the sidecars.
            return [];
        }

        var matches = new List<string>();
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileName(file);
            if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;

            // Ignore "Movie2.srt" style files that only share a prefix.
            var suffix = name[baseName.Length..];
            if (suffix.Length > 0 && suffix[0] != '.' && suffix[0] != '_' && suffix[0] != '-') continue;

            matches.Add(file);
        }
        return matches;
    }

    /// <summary>Turns "Movie.eng.srt" / "Movie.en.forced.srt" into "eng" / "en".</summary>
    private static string? ExtractLanguage(string fileName, string baseName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length <= baseName.Length) return null;

        var suffix = stem[(baseName.Length + 1)..];
        foreach (var token in suffix.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length is 2 or 3 && token.All(char.IsLetter))
            {
                return token.ToLowerInvariant();
            }
        }
        return null;
    }

    private static int LanguageRank(string? language)
    {
        if (language is null) return PreferredLanguages.Length - 1;
        var index = Array.IndexOf(PreferredLanguages, language.ToLowerInvariant());
        return index >= 0 ? index : PreferredLanguages.Length - 1;
    }
}

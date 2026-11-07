namespace Lantern.Core.Parsing;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lantern.Core.Models;

/// <summary>
/// Extracts title/year/season/episode information from file and folder names.
/// Deliberately offline: no metadata provider is contacted, ever.
/// </summary>
public static partial class MediaNameParser
{
    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".vob", ".flv", ".wmv",
    ];

    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"];

    /// <summary>Tokens that mark the end of the title in a release name.</summary>
    private static readonly string[] JunkTokens =
    [
        "1080p", "1080i", "720p", "576p", "480p", "2160p", "4k", "8k", "uhd", "hdrip", "bdrip", "brrip", "bluray",
        "blu-ray", "bdremux", "remux", "webrip", "web-dl", "webdl", "web", "hdtv", "dvdrip", "dvd", "hdr", "hdr10",
        "hdr10+", "dv", "dolbyvision", "dolby", "vision", "sdr", "10bit", "8bit", "x264", "x265", "h264", "h265",
        "hevc", "avc", "av1", "vp9", "divx", "xvid", "aac", "ac3", "eac3", "ddp", "dts", "dtshd", "truehd", "atmos",
        "flac", "opus", "mp3", "proper", "repack", "internal", "extended", "uncut", "remastered", "dual", "multi",
        "subs", "esub", "yts", "yify", "rarbg", "psa", "evo", "fgt", "ntb", "cmrg", "galaxyrg", "hmax", "amzn",
        "nf", "dsnp", "atvp",
    ];

    private static readonly HashSet<string> JunkSet = new(JunkTokens, StringComparer.OrdinalIgnoreCase);

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSubtitleFile(string path) =>
        SubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsIgnoredDirectoryName(string name) =>
        name.StartsWith('.')
        || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
        || name.Equals("sample", StringComparison.OrdinalIgnoreCase)
        || name.Equals("extras", StringComparison.OrdinalIgnoreCase)
        || name.Equals("featurettes", StringComparison.OrdinalIgnoreCase)
        || name.Equals("behind the scenes", StringComparison.OrdinalIgnoreCase)
        || name.Equals("@eadir", StringComparison.OrdinalIgnoreCase)
        || name.Equals("lost+found", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for files that should be skipped (samples, trailers, junk).</summary>
    public static bool IsIgnoredFile(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.StartsWith("sample")
            || lower.Contains(".sample.")
            || lower.Contains("rarbg.com")
            || lower.Contains("www.")
            || lower.Equals("thumbs.db")
            || lower.EndsWith(".part")
            || lower.EndsWith(".tmp");
    }

    /// <summary>Parses a video file path into a media item description.</summary>
    public static ParsedMediaName Parse(string path, LibraryFolderType folderType = LibraryFolderType.Auto)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var result = new ParsedMediaName { CleanedName = CleanName(fileName) };

        var (season, episode, episodeCode) = ExtractEpisode(fileName, directory, folderType);
        if (episode is not null)
        {
            result.Kind = MediaKind.Episode;
            result.SeasonNumber = season ?? 1;
            result.EpisodeNumber = episode;
            result.EpisodeCode = episodeCode ?? $"S{result.SeasonNumber:00}E{episode:00}";
            result.ShowTitle = ExtractShowTitle(directory, fileName);
            result.Title = result.CleanedName.Length > 2 ? result.CleanedName : result.EpisodeCode;
            result.EpisodeTitle = result.Title;
        }
        else
        {
            result.Kind = MediaKind.Movie;
            result.Title = string.IsNullOrWhiteSpace(result.CleanedName) ? fileName : result.CleanedName;
        }

        result.Year = ExtractYear(fileName) ?? ExtractYear(Path.GetFileName(directory));
        var sortSource = result.Kind == MediaKind.Episode ? result.ShowTitle ?? result.Title : result.Title;
        result.SortTitle = BuildSortTitle(sortSource);
        return result;
    }

    /// <summary>Turns "The.Movie.2017.1080p.BluRay.x265-GRP" into "The Movie".</summary>
    public static string CleanName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(ch is '.' or '_' or '-' ? ' ' : ch);
        }

        var text = BracketedRegex().Replace(sb.ToString(), " ");
        text = YearTokenRegex().Replace(text, " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var w in words)
        {
            var normalized = w.Trim().Trim(',', '\'', '"', '+');
            if (normalized.Length == 0) continue;
            if (JunkSet.Contains(normalized)) break;
            kept.Add(normalized);
        }

        var result = MultiSpaceRegex().Replace(string.Join(' ', kept).Trim(), " ").Trim('-', ' ');
        return result.Length == 0 ? raw : result;
    }

    public static string BuildSortTitle(string title)
    {
        var trimmed = title.Trim();
        foreach (var article in new[] { "the ", "a ", "an ", "der ", "die ", "das ", "le ", "la ", "les " })
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[article.Length..].Trim();
            }
        }
        return trimmed;
    }

    private static int? ExtractYear(string text)
    {
        foreach (Match m in YearRegex().Matches(text))
        {
            if (int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                && y is >= 1900 and <= 2100)
            {
                return y;
            }
        }
        return null;
    }

    private static (int? Season, int? Episode, string? Code) ExtractEpisode(
        string fileName, string directory, LibraryFolderType folderType)
    {
        var sxxExx = SxxExxRegex().Match(fileName);
        if (sxxExx.Success)
        {
            var s = int.Parse(sxxExx.Groups[1].Value, CultureInfo.InvariantCulture);
            var e = int.Parse(sxxExx.Groups[2].Value, CultureInfo.InvariantCulture);
            return (s, e, $"S{s:00}E{e:00}");
        }

        var alt = AltEpisodeRegex().Match(fileName);
        if (alt.Success)
        {
            var s = int.Parse(alt.Groups[1].Value, CultureInfo.InvariantCulture);
            var e = int.Parse(alt.Groups[2].Value, CultureInfo.InvariantCulture);
            return (s, e, $"S{s:00}E{e:00}");
        }

        if (folderType == LibraryFolderType.TvShows)
        {
            var season = ExtractSeasonNumber(directory);
            var epOnly = EpOnlyRegex().Match(fileName);
            if (epOnly.Success)
            {
                var e = int.Parse(epOnly.Groups[1].Value, CultureInfo.InvariantCulture);
                return (season ?? 1, e, $"S{season ?? 1:00}E{e:00}");
            }
        }

        return (null, null, null);
    }

    /// <summary>Detects "Season 3", "Series 3", "S03" folder names.</summary>
    public static int? ExtractSeasonNumber(string directory)
    {
        var folder = Path.GetFileName(directory.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder)) return null;

        var m = SeasonFolderRegex().Match(folder);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string ExtractShowTitle(string directory, string fileName)
    {
        var current = new DirectoryInfo(directory.TrimEnd('\\', '/'));
        var candidates = new List<string>();
        for (var i = 0; i < 3 && current is not null; i++, current = current.Parent)
        {
            if (string.IsNullOrEmpty(current.Name)) break;
            candidates.Add(current.Name);
        }

        string[] genericNames = ["Movies", "TV", "TV Shows", "Media", "Video", "Videos", "Series"];
        foreach (var c in candidates)
        {
            if (SeasonFolderRegex().IsMatch(c)) continue;
            if (c.Length <= 1) continue;
            if (genericNames.Contains(c, StringComparer.OrdinalIgnoreCase)) continue;
            return CleanName(c);
        }

        var name = fileName;
        var m = SxxExxRegex().Match(name);
        if (m.Success) name = name[..m.Index];
        var cleaned = CleanName(name);
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown Show" : cleaned;
    }

    [GeneratedRegex(@"s(\d{1,2})\s*e(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SxxExxRegex();

    [GeneratedRegex(@"(\d{1,2})x(\d{2,3})")]
    private static partial Regex AltEpisodeRegex();

    [GeneratedRegex(@"^\s*(?:ep|e|part)?\s*(\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpOnlyRegex();

    [GeneratedRegex(@"(?:season|series|s)\s*(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"\(?(19|20)\d{2}\)?")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearTokenRegex();

    [GeneratedRegex(@"[\[\{\(][^\]\}\)]*[\]\}\)]")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}

/// <summary>Result of <see cref="MediaNameParser.Parse"/>.</summary>
public sealed class ParsedMediaName
{
    public string Title { get; set; } = string.Empty;
    public string? SortTitle { get; set; }
    public string? CleanedName { get; set; }
    public int? Year { get; set; }
    public MediaKind Kind { get; set; } = MediaKind.Movie;
    public string? ShowTitle { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeCode { get; set; }
    public string? EpisodeTitle { get; set; }
}

namespace Lantern.Core.Database;

/// <summary>
/// Connection-string/path helpers for the SQLite database file. Kept in Core so
/// both the server and the tests can resolve paths identically.
/// </summary>
public static class DatabasePaths
{
    /// <summary>Folder under %LOCALAPPDATA% used for the database, cache and logs.</summary>
    public static string DefaultDataDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Lantern");
        }
    }

    public static string DefaultDatabasePath => Path.Combine(DefaultDataDirectory, "lantern.db");

    public static string DefaultCachePath => Path.Combine(DefaultDataDirectory, "cache");

    public static string DefaultThumbnailPath => Path.Combine(DefaultCachePath, "thumbnails");

    public static string DefaultTranscodePath => Path.Combine(DefaultCachePath, "transcode");

    public static string DefaultLogPath => Path.Combine(DefaultDataDirectory, "logs");

    /// <summary>Resolves the configured path, creating the parent folder if needed.</summary>
    public static string Resolve(string? configured, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? fallback : Environment.ExpandEnvironmentVariables(configured);
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return full;
    }

    /// <summary>The file name of a local copy of a remote path encoded for a URL.</summary>
    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        var name = sb.ToString().Trim();
        return name.Length == 0 ? "unnamed" : name;
    }
}

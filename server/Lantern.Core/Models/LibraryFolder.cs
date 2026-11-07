namespace Lantern.Core.Models;

/// <summary>A folder on the server machine that is scanned for media.</summary>
public sealed class LibraryFolder
{
    public long Id { get; set; }

    /// <summary>Absolute folder path (e.g. D:\Movies).</summary>
    public required string Path { get; set; }

    /// <summary>Optional display name. Defaults to the folder name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether sub folders are scanned.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>Allows disabling a folder without deleting it from the library.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Type hint used for parsing (Auto lets the scanner detect TV / movies).</summary>
    public LibraryFolderType Type { get; set; } = LibraryFolderType.Auto;

    public bool WatchForChanges { get; set; } = true;

    public DateTime AddedUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }

    /// <summary>Number of media files found during the last scan.</summary>
    public int LastScanFileCount { get; set; }

    public string Name => string.IsNullOrWhiteSpace(DisplayName)
        ? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path
        : DisplayName!;
}

public enum LibraryFolderType
{
    Auto = 0,
    Movies = 1,
    TvShows = 2,
    Mixed = 3,
}

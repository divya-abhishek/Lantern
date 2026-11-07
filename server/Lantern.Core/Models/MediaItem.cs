namespace Lantern.Core.Models;

/// <summary>
/// A logical media entry shown in the library. Movies map 1:1 to a file, TV
/// episodes map to a single episode file but carry show/season metadata so the
/// API and database can grow into full TV support without schema changes.
/// </summary>
public sealed class MediaItem
{
    public long Id { get; set; }

    /// <summary>Display title (movie title or episode title).</summary>
    public required string Title { get; set; }

    /// <summary>Title used for alphabetical sorting (article prefixes stripped).</summary>
    public string? SortTitle { get; set; }

    /// <summary>Release year when detectable from the folder/file name.</summary>
    public int? Year { get; set; }

    public MediaKind Kind { get; set; } = MediaKind.Movie;

    /// <summary>Show name for episodes.</summary>
    public string? ShowTitle { get; set; }

    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? EpisodeTitle { get; set; }

    /// <summary>Season/episode label such as "S01E04" (display helper, also persisted for search).</summary>
    public string? EpisodeCode { get; set; }

    public string? Description { get; set; }

    /// <summary>Folder the item was discovered in.</summary>
    public long? FolderId { get; set; }

    /// <summary>Relative path of the primary file below its library folder.</summary>
    public string? RelativePath { get; set; }

    /// <summary>Primary file used for playback (highest quality when several exist).</summary>
    public long PrimaryFileId { get; set; }

    public DateTime AddedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public int PlayCount { get; set; }

    /// <summary>All files belonging to this item.</summary>
    public IReadOnlyList<MediaFileRecord> Files { get; set; } = [];

    public MediaFileRecord? PrimaryFile
    {
        get
        {
            foreach (var f in Files)
            {
                if (f.Id == PrimaryFileId) return f;
            }
            return Files.Count > 0 ? Files[0] : null;
        }
    }

    /// <summary>Percentage (0..100) of the primary file position that was watched last time.</summary>
    public double? WatchedPercent { get; set; }
}

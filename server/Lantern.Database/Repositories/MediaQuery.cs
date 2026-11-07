using Lantern.Core.Models;

namespace Lantern.Database.Repositories;

/// <summary>Sort orders supported by the library queries.</summary>
public enum MediaSortOrder
{
    TitleAsc = 0,
    TitleDesc = 1,
    AddedDesc = 2,
    AddedAsc = 3,
    YearDesc = 4,
    DurationDesc = 5,
    SizeDesc = 6,
    LastPlayedDesc = 7,
}

/// <summary>Parameters of a library/list query (translated to SQL by the repository).</summary>
public sealed class MediaQuery
{
    /// <summary>Free text matched against title, show title and episode code.</summary>
    public string? Search { get; set; }

    public MediaKind? Kind { get; set; }
    public long? FolderId { get; set; }
    public string? ShowTitle { get; set; }
    public int? SeasonNumber { get; set; }
    public bool? Watched { get; set; }
    public bool? Hdr { get; set; }
    public int? MinYear { get; set; }
    public int? MaxYear { get; set; }
    public int Limit { get; set; } = 200;
    public int Offset { get; set; }
    public MediaSortOrder Sort { get; set; } = MediaSortOrder.TitleAsc;
    public bool IncludeFiles { get; set; }

    public const int MaxLimit = 2000;
}

/// <summary>A page of results plus the total row count for the same filter.</summary>
public sealed class MediaPage
{
    public List<MediaItem> Items { get; init; } = [];
    public int Total { get; init; }
}

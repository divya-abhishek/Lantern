namespace Lantern.Core.Dtos;

using Lantern.Core.Models;

/// <summary>Generic paged list response.</summary>
public sealed class PagedDto<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public bool HasMore => Offset + Items.Count < Total;
}

/// <summary>A library folder as returned by GET /api/v1/library/folders.</summary>
public sealed class FolderDto
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public LibraryFolderType Type { get; set; }
    public bool Recursive { get; set; }
    public bool Enabled { get; set; }
    public bool WatchForChanges { get; set; }
    public int MediaCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public bool Exists { get; set; }
}

/// <summary>Response of GET /api/v1/library.</summary>
public sealed class LibraryDto
{
    public LibraryCountsDto Counts { get; set; } = new();
    public List<FolderDto> Folders { get; set; } = [];
    public ScanStatusDto Scanning { get; set; } = new();
    public List<MediaSummaryDto> ContinueWatching { get; set; } = [];
    public List<MediaSummaryDto> RecentlyAdded { get; set; } = [];
    public DateTime? LastScanUtc { get; set; }
}

/// <summary>Live scan progress (also pushed over the WebSocket).</summary>
public sealed class ScanStatusDto
{
    public bool Active { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? CurrentFile { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public bool Cancelled { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Errors { get; set; }
    public List<string> Messages { get; set; } = [];

    public double Percent => Total > 0 ? Math.Clamp(Processed * 100.0 / Total, 0, 100) : 0;
    public string Label => Active
        ? $"Scanning... {Processed} / {Total}"
        : FinishedUtc is not null ? $"Last scan {FinishedUtc:g}" : "Idle";
}

/// <summary>Request body for POST /api/v1/library/scan.</summary>
public sealed class ScanRequestDto
{
    public bool Force { get; set; }
    public List<long>? FolderIds { get; set; }
}

/// <summary>Request body for POST /api/v1/library/folders.</summary>
public sealed class AddFolderRequestDto
{
    public required string Path { get; set; }
    public string? DisplayName { get; set; }
    public bool Recursive { get; set; } = true;
    public bool WatchForChanges { get; set; } = true;
    public LibraryFolderType Type { get; set; } = LibraryFolderType.Auto;
    public bool ScanNow { get; set; } = true;
}

/// <summary>Grouping used by the library browser (shows/seasons).</summary>
public sealed class ShowGroupDto
{
    public required string ShowTitle { get; set; }
    public int EpisodeCount { get; set; }
    public List<int> Seasons { get; set; } = [];
    public string? ThumbnailUrl { get; set; }
    public MediaSummaryDto? Latest { get; set; }
}

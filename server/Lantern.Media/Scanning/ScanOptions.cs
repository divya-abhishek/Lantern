using Lantern.Core.Models;

namespace Lantern.Media.Scanning;

/// <summary>Parameters of a library scan.</summary>
public sealed class ScanOptions
{
    /// <summary>Re-probe files even when size/timestamp did not change.</summary>
    public bool Force { get; set; }

    /// <summary>Restrict the scan to these folders (null = all enabled folders).</summary>
    public List<long>? FolderIds { get; set; }

    /// <summary>Run ffprobe on new/changed files (false = index file names only).</summary>
    public bool ProbeMedia { get; set; } = true;

    /// <summary>Number of files probed in parallel.</summary>
    public int MaxParallelProbes { get; set; } = 4;

    /// <summary>Skip probing files smaller than this (bytes) – guards against samples/trailers.</summary>
    public long MinimumFileSize { get; set; } = 8 * 1024 * 1024;
}

/// <summary>What a scan did, shown in the PC UI and returned by POST /library/scan.</summary>
public sealed class ScanReport
{
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }
    public int FilesSeen { get; set; }
    public int FilesAdded { get; set; }
    public int FilesUpdated { get; set; }
    public int FilesRemoved { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsRemoved { get; set; }
    public int Errors { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Messages { get; set; } = [];

    public TimeSpan Duration => FinishedUtc - StartedUtc;

    public void AddMessage(string message)
    {
        if (Messages.Count < 200)
        {
            Messages.Add(message);
        }
    }
}

/// <summary>One video file discovered on disk, before it is mapped to a media item.</summary>
internal sealed class ScannedFile
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required long FolderId { get; init; }
    public required FileInfo Info { get; init; }

    /// <summary>Existing database row when the file was already indexed.</summary>
    public MediaFileRecord? Known { get; set; }

    /// <summary>True when the file is new or its size/timestamp changed.</summary>
    public bool NeedsProbe { get; set; }

    /// <summary>Populated for files that had to be probed.</summary>
    public Probing.MediaProbeResult? Probe { get; set; }

    public long DurationMs => Probe?.DurationMs ?? Known?.DurationMs ?? 0;
    public long SizeBytes => Probe is { SizeBytes: > 0 } probe ? probe.SizeBytes : Info.Length;

    /// <summary>Identity used to group several files into one logical item.</summary>
    public string IdentityKey { get; set; } = string.Empty;
}

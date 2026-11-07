using Lantern.Core.Models;
using Lantern.Core.Parsing;

namespace Lantern.Media.Scanning;

public sealed partial class LibraryScanner
{
    /// <summary>Writes the discovered files into SQLite, one item per identity group.</summary>
    private async Task PersistFilesAsync(
        LibraryFolder folder, List<ScannedFile> files, ScanReport report, CancellationToken ct)
    {
        var claimed = new HashSet<long>();

        foreach (var group in files.GroupBy(f => f.IdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var versions = group
                .OrderByDescending(f => f.SizeBytes)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var primary = versions[0];
            var parsed = MediaNameParser.Parse(primary.Path, folder.Type);

            var item = await ResolveItemAsync(folder, versions, primary, parsed, claimed, ct).ConfigureAwait(false);
            var isNew = item.Id == 0;

            item.PrimaryFileId = 0; // assigned once the file rows exist
            if (isNew)
            {
                item.Id = await _media.InsertItemAsync(item, ct).ConfigureAwait(false);
                report.ItemsAdded++;
            }
            else
            {
                await _media.UpdateItemAsync(item, ct).ConfigureAwait(false);
            }
            claimed.Add(item.Id);

            long primaryFileId = 0;
            foreach (var file in versions)
            {
                var record = BuildFileRecord(item, file, report);
                var fileId = await _media.UpsertFileAsync(record, ct).ConfigureAwait(false);

                if (file.Known is null) report.FilesAdded++;
                else if (file.NeedsProbe) report.FilesUpdated++;

                if (string.Equals(file.Path, primary.Path, StringComparison.OrdinalIgnoreCase)) primaryFileId = fileId;
            }

            var removed = await _media
                .DeleteMissingFilesAsync(item.Id, versions.Select(v => v.Path).ToList(), ct)
                .ConfigureAwait(false);
            report.FilesRemoved += removed;

            if (primaryFileId != 0 && item.PrimaryFileId != primaryFileId)
            {
                await _media.SetPrimaryFileAsync(item.Id, primaryFileId, ct).ConfigureAwait(false);
            }
        }

        var orphaned = await _media.DeleteItemsWithoutFilesAsync(folder.Id, ct).ConfigureAwait(false);
        report.ItemsRemoved += orphaned.Count;

        _status.Added = report.FilesAdded;
        _status.Updated = report.FilesUpdated;
        _status.Removed = report.FilesRemoved;
        _status.Errors = report.Errors;
        Publish();
    }

    /// <summary>Finds the existing item for a group, or fabricates a fresh one.</summary>
    private async Task<MediaItem> ResolveItemAsync(
        LibraryFolder folder,
        List<ScannedFile> versions,
        ScannedFile primary,
        ParsedMediaName parsed,
        HashSet<long> claimed,
        CancellationToken ct)
    {
        MediaItem? item = null;

        var knownItemId = versions
            .Select(v => v.Known?.MediaId ?? 0)
            .FirstOrDefault(id => id > 0 && !claimed.Contains(id));

        if (knownItemId > 0)
        {
            item = await _media.GetItemAsync(knownItemId, includeFiles: false, ct).ConfigureAwait(false);
        }

        if (item is null && !IsClaimedPath(claimed, primary))
        {
            item = await _media.FindItemAsync(folder.Id, primary.RelativePath, ct).ConfigureAwait(false);
            if (item is not null && claimed.Contains(item.Id))
            {
                item = null;
            }
        }

        item ??= new MediaItem { Title = parsed.Title, AddedUtc = DateTime.UtcNow };

        item.Title = string.IsNullOrWhiteSpace(parsed.Title)
            ? Path.GetFileNameWithoutExtension(primary.Path)
            : parsed.Title;
        item.SortTitle = parsed.SortTitle ?? item.Title;
        item.Year = parsed.Year;
        item.Kind = parsed.Kind;
        item.ShowTitle = parsed.ShowTitle;
        item.SeasonNumber = parsed.SeasonNumber;
        item.EpisodeNumber = parsed.EpisodeNumber;
        item.EpisodeTitle = parsed.EpisodeTitle;
        item.EpisodeCode = parsed.EpisodeCode;
        item.FolderId = folder.Id;
        item.RelativePath = primary.RelativePath;
        return item;
    }

    /// <summary>The relative path lookup is only used for brand new items; claimed ids win.</summary>
    private static bool IsClaimedPath(HashSet<long> claimed, ScannedFile primary) =>
        claimed.Count > 0 && primary.Known is not null && claimed.Contains(primary.Known.MediaId);

    /// <summary>Builds the row to store, reusing the previous values for unchanged files.</summary>
    private static MediaFileRecord BuildFileRecord(MediaItem item, ScannedFile file, ScanReport report)
    {
        var sidecars = SubtitleSidecars.Find(file.Path);

        if (file.Probe is { Succeeded: true } probe)
        {
            var probed = probe.ToRecord(item.Id, file.Path, file.Info.LastWriteTimeUtc, DateTime.UtcNow);
            probed.SubtitleTracks = Merge(probe.SubtitleTracks, sidecars);
            return probed;
        }

        if (file.Probe is { } failed)
        {
            report.AddMessage($"Indexed without technical details: {file.RelativePath} ({failed.Error})");
        }

        if (file.Known is { } known)
        {
            known.MediaId = item.Id;
            known.SizeBytes = file.Info.Length;
            known.LastWriteUtc = file.Info.LastWriteTimeUtc;
            known.LastScannedUtc = DateTime.UtcNow;
            known.SubtitleTracks = Merge(known.SubtitleTracks, sidecars);
            return known;
        }

        return new MediaFileRecord
        {
            MediaId = item.Id,
            Path = file.Path,
            SizeBytes = file.Info.Length,
            LastWriteUtc = file.Info.LastWriteTimeUtc,
            LastScannedUtc = DateTime.UtcNow,
            SubtitleTracks = sidecars,
        };
    }

    /// <summary>Embedded tracks are re-read on every probe; external ones are re-discovered from disk.</summary>
    private static List<SubtitleTrackInfo> Merge(
        IReadOnlyList<SubtitleTrackInfo> embedded, List<SubtitleTrackInfo> sidecars)
    {
        var merged = embedded.Where(t => !t.IsExternal).ToList();
        merged.AddRange(sidecars);
        return merged;
    }
}

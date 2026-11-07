using Lantern.Core.Models;
using Lantern.Core.Parsing;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Scanning;

public sealed partial class LibraryScanner
{
    /// <summary>Lists every candidate video file of a folder and flags what needs a probe.</summary>
    private async Task<List<ScannedFile>> DiscoverFolderAsync(
        LibraryFolder folder, ScanOptions options, ScanReport report, CancellationToken ct)
    {
        var known = await _media.GetFileRecordsAsync(folder.Id, ct).ConfigureAwait(false);
        var files = new List<ScannedFile>();

        foreach (var path in EnumerateVideoFiles(folder, report))
        {
            ct.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Errors++;
                report.AddMessage($"Cannot read {path}");
                _logger.LogDebug(ex, "Cannot read {Path}", path);
                continue;
            }

            known.TryGetValue(info.FullName, out var record);
            var parsed = MediaNameParser.Parse(info.FullName, folder.Type);
            files.Add(new ScannedFile
            {
                Path = info.FullName,
                RelativePath = Path.GetRelativePath(folder.Path, info.FullName),
                FolderId = folder.Id,
                Info = info,
                Known = record,
                NeedsProbe = options.ProbeMedia
                    && (options.Force || record is null || HasChanged(record, info) || ContainerIsStale(record)),
                IdentityKey = IdentityOf(parsed),
            });
        }

        report.FilesSeen += files.Count;
        return files;
    }

    /// <summary>Walks the folder (respecting the recursive flag) and filters obvious junk.</summary>
    private IEnumerable<string> EnumerateVideoFiles(LibraryFolder folder, ScanReport report)
    {
        var pending = new Stack<string>();
        pending.Push(folder.Path);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Errors++;
                report.AddMessage($"Skipped folder {directory}: {ex.Message}");
                _logger.LogDebug(ex, "Cannot enumerate {Directory}", directory);
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (folder.Recursive && !MediaNameParser.IsIgnoredDirectoryName(name))
                    {
                        pending.Push(entry);
                    }
                    continue;
                }

                if (MediaNameParser.IsIgnoredFile(name) || !MediaNameParser.IsVideoFile(entry)) continue;
                yield return entry;
            }
        }
    }

    /// <summary>A file counts as changed when it was re-written or was never probed successfully.</summary>
    private static bool HasChanged(MediaFileRecord record, FileInfo info) =>
        record.SizeBytes != info.Length
        || Math.Abs((record.LastWriteUtc - info.LastWriteTimeUtc).TotalSeconds) > 2
        || record.DurationMs == 0;

    /// <summary>
    /// True when the stored container cannot describe the file, so it is probed again and rewritten.
    /// The case this exists for: an AVI that was indexed while the container map called AVI "mkv".
    /// </summary>
    private static bool ContainerIsStale(MediaFileRecord record) =>
        string.Equals(Path.GetExtension(record.Path), ".avi", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(record.Container, CodecNames.Avi, StringComparison.OrdinalIgnoreCase);

    /// <summary>Groups files that describe the same logical item (same movie or same episode).</summary>
    private static string IdentityOf(ParsedMediaName parsed)
    {
        if (parsed.Kind == MediaKind.Episode)
        {
            var show = (parsed.ShowTitle ?? parsed.Title).Trim().ToLowerInvariant();
            return $"ep|{show}|{parsed.SeasonNumber ?? 1}|{parsed.EpisodeNumber ?? 0}";
        }

        var title = parsed.Title.Trim().ToLowerInvariant();
        return $"mv|{title}|{parsed.Year?.ToString() ?? "-"}";
    }

    /// <summary>Runs ffprobe over the new/changed files, a few at a time.</summary>
    private async Task ProbeChangedFilesAsync(
        List<ScannedFile> files, ScanOptions options, LanternSettings settings,
        ScanReport report, CancellationToken ct)
    {
        var pending = files.Where(f => f.NeedsProbe).ToList();
        if (pending.Count == 0)
        {
            _status.Processed += files.Count;
            _status.Errors = report.Errors;
            Publish();
            return;
        }

        _status.Processed += files.Count - pending.Count;
        Publish();

        using var throttle = new SemaphoreSlim(Math.Clamp(options.MaxParallelProbes, 1, 8));
        var tasks = pending.Select(async file =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                file.Probe = await _prober.ProbeAsync(file.Path, settings, ct).ConfigureAwait(false);
                if (!file.Probe.Succeeded)
                {
                    lock (report)
                    {
                        report.Errors++;
                        report.AddMessage($"ffprobe: {Path.GetFileName(file.Path)} – {file.Probe.Error}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (report)
                {
                    report.Errors++;
                    report.AddMessage($"ffprobe crashed on {Path.GetFileName(file.Path)}: {ex.Message}");
                }
            }
            finally
            {
                lock (report)
                {
                    _status.Processed++;
                    _status.CurrentFile = file.RelativePath;
                    _status.Errors = report.Errors;
                    Publish();
                }
                throttle.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

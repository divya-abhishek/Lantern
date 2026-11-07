using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Core.Parsing;
using Lantern.Database.Repositories;
using Lantern.Media.Probing;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Scanning;

/// <summary>
/// Walks the configured library folders, groups files into logical items,
/// probes what changed and writes the result into SQLite.
/// </summary>
public sealed partial class LibraryScanner
{
    private readonly MediaRepository _media;
    private readonly LibraryFolderRepository _folders;
    private readonly FfprobeRunner _prober;
    private readonly ILogger<LibraryScanner> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private ScanStatusDto _status = new();

    public LibraryScanner(
        MediaRepository media,
        LibraryFolderRepository folders,
        FfprobeRunner prober,
        ILogger<LibraryScanner> logger)
    {
        _media = media;
        _folders = folders;
        _prober = prober;
        _logger = logger;
    }

    /// <summary>Raised whenever the progress numbers change (used by the API/WebSocket hub).</summary>
    public event EventHandler<ScanStatusDto>? Progress;

    /// <summary>Snapshot of the current/last scan.</summary>
    public ScanStatusDto Status => _status;

    public bool IsScanning { get; private set; }

    public DateTime? LastFinishedUtc => _status.FinishedUtc;

    /// <summary>Scans every enabled folder (or the requested subset). Throws when a scan is running.</summary>
    public async Task<ScanReport> ScanAsync(
        ScanOptions options, LanternSettings settings, CancellationToken ct = default)
    {
        if (!await _scanGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A library scan is already running.");
        }

        var report = new ScanReport();
        try
        {
            IsScanning = true;
            _status = new ScanStatusDto { Active = true, StartedUtc = report.StartedUtc };
            Publish();

            var folders = await _folders.GetAllAsync(enabledOnly: true, ct).ConfigureAwait(false);
            if (options.FolderIds is { Count: > 0 })
            {
                folders = folders.Where(f => options.FolderIds.Contains(f.Id)).ToList();
            }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder.Path))
                {
                    report.AddMessage($"Folder not found: {folder.Path}");
                    _logger.LogWarning("Library folder missing: {Path}", folder.Path);
                    continue;
                }

                _status.CurrentFile = folder.Path;
                Publish();

                var files = await DiscoverFolderAsync(folder, options, report, ct).ConfigureAwait(false);
                if (files.Count == 0)
                {
                    await _folders.MarkScannedAsync(folder.Id, 0, DateTime.UtcNow, ct).ConfigureAwait(false);
                    continue;
                }

                _status.Total += files.Count;
                Publish();

                await ProbeChangedFilesAsync(files, options, settings, report, ct).ConfigureAwait(false);
                await PersistFilesAsync(folder, files, report, ct).ConfigureAwait(false);
                await _folders.MarkScannedAsync(folder.Id, files.Count, DateTime.UtcNow, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            _status.Cancelled = true;
            report.AddMessage("Scan cancelled.");
        }
        finally
        {
            report.FinishedUtc = DateTime.UtcNow;
            IsScanning = false;
            _status.Active = false;
            _status.FinishedUtc = report.FinishedUtc;
            _status.Cancelled = report.Cancelled;
            _status.Processed = _status.Total;
            Publish();
            _scanGate.Release();
        }

        _logger.LogInformation(
            "Scan finished in {Seconds:0.0}s: {Seen} files, {Added} added, {Updated} updated, {Removed} removed, {Errors} errors",
            report.Duration.TotalSeconds, report.FilesSeen, report.FilesAdded, report.FilesUpdated,
            report.FilesRemoved, report.Errors);
        return report;
    }

    /// <summary>Applies a single file/folder change (used by the filesystem watcher).</summary>
    public async Task<ScanReport> ScanFolderAsync(
        long folderId, ScanOptions options, LanternSettings settings, CancellationToken ct = default)
    {
        var scoped = new ScanOptions
        {
            Force = options.Force,
            FolderIds = [folderId],
            ProbeMedia = options.ProbeMedia,
            MaxParallelProbes = options.MaxParallelProbes,
            MinimumFileSize = options.MinimumFileSize,
        };
        return await ScanAsync(scoped, settings, ct).ConfigureAwait(false);
    }

    private void Publish()
    {
        var handler = Progress;
        if (handler is null) return;
        try
        {
            handler(this, _status);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scan progress handler failed");
        }
    }
}

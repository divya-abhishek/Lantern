using Lantern.Core.Dtos;
using Lantern.Database.Repositories;
using Lantern.Media.Scanning;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// Owns the "scan the library" use case: a background scan that pushes progress over the
/// WebSocket, or a blocking scan for callers that want the report.
/// </summary>
public sealed class ScanCoordinator
{
    private readonly LibraryScanner _scanner;
    private readonly SettingsStore _settings;
    private readonly MediaRepository _media;
    private readonly ConnectionHub _hub;
    private readonly ILogger<ScanCoordinator> _logger;

    public ScanCoordinator(
        LibraryScanner scanner,
        SettingsStore settings,
        MediaRepository media,
        ConnectionHub hub,
        ILogger<ScanCoordinator> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _media = media;
        _hub = hub;
        _logger = logger;
    }

    public ScanStatusDto Status => _scanner.Status;

    public bool IsScanning => _scanner.IsScanning;

    /// <summary>Runs a scan to completion and returns its report.</summary>
    public async Task<ScanReport> RunAsync(ScanOptions options, CancellationToken ct = default)
    {
        var report = await _scanner.ScanAsync(options, _settings.Current, ct).ConfigureAwait(false);
        await PublishAsync(report, ct).ConfigureAwait(false);
        return report;
    }

    /// <summary>Starts a scan in the background; progress arrives over the WebSocket.</summary>
    public void Start(ScanOptions options) => _ = Task.Run(() => RunSafeAsync(options), CancellationToken.None);

    private async Task RunSafeAsync(ScanOptions options)
    {
        try
        {
            await RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("Scan request ignored: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library scan failed");
        }
    }

    private async Task PublishAsync(ScanReport report, CancellationToken ct)
    {
        try
        {
            var counts = await _media.GetCountsAsync(ct).ConfigureAwait(false);
            await _hub.BroadcastLibraryChangedAsync(counts, report.FilesAdded, report.FilesRemoved)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Publishing the library change failed");
        }
    }
}

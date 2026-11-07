using Lantern.Core.Dtos;
using Lantern.Media.Scanning;
using Lantern.Media.Tooling;
using Lantern.Media.Transcoding;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// Startup and shutdown of everything that is not an HTTP endpoint: ffmpeg detection, the
/// transcode reaper, progress forwarding to the WebSocket hub and the startup scan.
/// </summary>
public sealed class ServerLifecycle : IHostedService
{
    private readonly ServerRuntime _runtime;
    private readonly SettingsStore _settings;
    private readonly FfmpegLocator _locator;
    private readonly TranscodeSessionManager _transcode;
    private readonly LibraryScanner _scanner;
    private readonly ScanCoordinator _scans;
    private readonly ConnectionHub _hub;
    private readonly ILogger<ServerLifecycle> _logger;

    public ServerLifecycle(
        ServerRuntime runtime,
        SettingsStore settings,
        FfmpegLocator locator,
        TranscodeSessionManager transcode,
        LibraryScanner scanner,
        ScanCoordinator scans,
        ConnectionHub hub,
        ILogger<ServerLifecycle> logger)
    {
        _runtime = runtime;
        _settings = settings;
        _locator = locator;
        _transcode = transcode;
        _scanner = scanner;
        _scans = scans;
        _hub = hub;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        _runtime.Tools = await _locator
            .ResolveAsync(settings, forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);

        _transcode.StartCleanupLoop();

        _scanner.Progress += OnScanProgress;
        _transcode.SessionUpdated += OnTranscodeChanged;
        _transcode.SessionFinished += OnTranscodeChanged;

        if (settings.ScanOnStartup)
        {
            _logger.LogInformation("Scanning the library at startup");
            _scans.Start(new ScanOptions { Force = false });
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scanner.Progress -= OnScanProgress;
        _transcode.SessionUpdated -= OnTranscodeChanged;
        _transcode.SessionFinished -= OnTranscodeChanged;
        return Task.CompletedTask;
    }

    private void OnScanProgress(object? sender, ScanStatusDto status) =>
        _ = _hub.BroadcastScanStatusAsync(status);

    private void OnTranscodeChanged(object? sender, TranscodeSession session) =>
        _ = _hub.BroadcastTranscodeAsync(session.ToDto());
}

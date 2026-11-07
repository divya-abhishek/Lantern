using System.Collections.Concurrent;
using Lantern.Core.Database;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Tooling;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Transcoding;
public sealed class TranscodeLimitException : Exception
{
    public TranscodeLimitException(string message) : base(message) { }
}
public sealed class TranscodeSessionManager : IAsyncDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<TranscodeSessionManager> _logger;
    private readonly ConcurrentDictionary<string, TranscodeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private Timer? _cleanupTimer;
    private bool _disposed;
    private readonly Dictionary<string, bool> _encoderProbes = new(StringComparer.OrdinalIgnoreCase);

    public TranscodeSessionManager(FfmpegLocator locator, ILogger<TranscodeSessionManager> logger)
    {
        _locator = locator;
        _logger = logger;
    }
    public int MaxConcurrentSessions { get; set; } = 2;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int DefaultSegmentSeconds { get; set; } = 4;
    public TimeSpan StaleFolderAge { get; set; } = TimeSpan.FromDays(1);

    public IReadOnlyCollection<TranscodeSession> Sessions => _sessions.Values.ToArray();

    public int RunningCount => _sessions.Values.Count(s => s.IsRunning);
    public event EventHandler<TranscodeSession>? SessionUpdated;
    public event EventHandler<TranscodeSession>? SessionFinished;
    public static string ResolveTranscodeRoot(LanternSettings settings)
    {
        var cache = DatabasePaths.Resolve(settings.CachePath, DatabasePaths.DefaultCachePath);
        return Path.Combine(cache, "transcode");
    }
    public async Task<TranscodeSession> StartAsync(
        MediaFileRecord file,
        PlaybackPlan plan,
        LanternSettings settings,
        string? clientId = null,
        string? title = null,
        long startMs = 0,
        int? segmentSeconds = null,
        CancellationToken ct = default)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var tools = await _locator.ResolveAsync(settings, false, ct).ConfigureAwait(false);
        if (!tools.CanTranscode)
        {
            throw new InvalidOperationException(tools.Error ?? "ffmpeg is not available for transcoding");
        }

        startMs = 0; // Complete-file cache; seeking is always a byte-range request.
        var segment = segmentSeconds is > 0 ? segmentSeconds.Value : DefaultSegmentSeconds;
        _cacheRoot = ResolveTranscodeRoot(settings);
        MaxCacheBytes = Math.Max(1, settings.TranscodeCacheMaxGb) * 1024L * 1024 * 1024;
        TrackRoot(_cacheRoot);
        var reuse = FindReusable(file, plan, clientId, startMs, segment);
        if (reuse is not null)
        {
            reuse.Touch();
            return reuse;
        }

        await _createGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            reuse = FindReusable(file, plan, clientId, startMs, segment);
            if (reuse is not null)
            {
                reuse.Touch();
                return reuse;
            }

            var completedPath = CachePath(file, plan, ResolveTranscodeRoot(settings));
            var cached = File.Exists(completedPath) && new FileInfo(completedPath).Length > 0;
            if (!cached && RunningCount >= MaxConcurrentSessions)
            {
                throw new TranscodeLimitException(
                    $"Already transcoding {RunningCount} stream(s); the limit is {MaxConcurrentSessions}.");
            }

            var id = Guid.NewGuid().ToString("N")[..12];
            var request = new TranscodeRequest
            {
                SourcePath = file.Path,
                OutputDirectory = Path.Combine(ResolveTranscodeRoot(settings), id),
                CompletedPath = completedPath,
                Plan = plan,
                StartMs = Math.Max(0, startMs),
                SourceDurationMs = file.DurationMs > 0 ? file.DurationMs : null,
                SourceFrameRate = file.FrameRate,
                SourceAudioChannels = ResolveAudioChannels(file, plan),
                SelectedAudioOrdinal = ResolveAudioOrdinal(file, plan),
                SegmentSeconds = segment,
                EncoderPreference = cached ? "libx264" : await ResolveEncoderAsync(tools, settings.TranscodeEncoder, ct).ConfigureAwait(false),
                ClientId = clientId,
                FileId = file.Id,
                MediaId = file.MediaId,
                Title = string.IsNullOrWhiteSpace(title) ? file.FileName : title!,
            };

            var session = new TranscodeSession(id, request, tools, _logger);
            session.Updated += OnSessionUpdated;
            session.Finished += OnSessionFinished;
            session.Start();

            if (session.State == TranscodeState.Failed)
            {
                session.Dispose();
                throw new InvalidOperationException(session.Error ?? "ffmpeg failed to start");
            }

            _sessions[id] = session;
            return session;
        }
        finally
        {
            _createGate.Release();
        }
    }

    private async Task<string> ResolveEncoderAsync(FfmpegTools tools, string preference, CancellationToken ct)
    {
        if (!string.Equals(preference, "auto", StringComparison.OrdinalIgnoreCase)) return preference;
        foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            if (!tools.SupportsEncoder(encoder)) continue;
            var key = tools.FfmpegPath + "|" + encoder;
            if (!_encoderProbes.TryGetValue(key, out var usable))
            {
                var result = await Lantern.Media.Processes.ProcessRunner.RunAsync(tools.FfmpegPath!,
                    ["-v", "error", "-f", "lavfi", "-i", "color=s=1920x1080:r=30", "-frames:v", "3", "-c:v", encoder, "-f", "null", "-"],
                    TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _encoderProbes[key] = usable = result.Success;
            }
            if (usable) return encoder;
        }
        return "libx264";
    }

    private static int? ResolveAudioChannels(MediaFileRecord file, PlaybackPlan plan)
    {
        var track = FindTrack(file, plan) ?? file.AudioTracks.FirstOrDefault(t => t.IsDefault) ?? file.AudioTracks.FirstOrDefault();
        return track?.Channels;
    }

    private static int? ResolveAudioOrdinal(MediaFileRecord file, PlaybackPlan plan)
    {
        if (plan.SelectedAudioStreamIndex is not { } index) return null;
        for (var i = 0; i < file.AudioTracks.Count; i++)
        {
            if (file.AudioTracks[i].Index == index) return i;
        }
        return null;
    }

    private static MediaTrackInfo? FindTrack(MediaFileRecord file, PlaybackPlan plan) =>
        plan.SelectedAudioStreamIndex is { } index
            ? file.AudioTracks.FirstOrDefault(t => t.Index == index)
            : null;

    private TranscodeSession? FindReusable(MediaFileRecord file, PlaybackPlan plan, string? clientId, long startMs, int segmentSeconds)
    {
        foreach (var session in _sessions.Values)
        {
            // Polling a failed preparation must report that failure, not launch an
            // endless sequence of identical encodes. Stop the session to retry.
            if (session.State == TranscodeState.Stopped) continue;
            if (session.State == TranscodeState.Completed && !session.Ready) continue;

            if (session.FileId != file.Id || session.CompletedPath != CachePath(file, plan, _cacheRoot!)) continue;
            if (!MatchesPlan(session.Plan, plan)) continue;
            if (session.StartMs != Math.Max(0, startMs)) continue;
            if (session.Request.SegmentSeconds != segmentSeconds) continue;
            if (!string.Equals(session.ClientId, clientId, StringComparison.OrdinalIgnoreCase)) continue;
            return session;
        }
        return null;
    }
    private static bool MatchesPlan(PlaybackPlan left, PlaybackPlan right) =>
        left.Mode == right.Mode
        && left.AudioOnly == right.AudioOnly
        && SameText(left.VideoAction, right.VideoAction)
        && SameText(left.AudioAction, right.AudioAction)
        && SameText(left.OutputVideoCodec, right.OutputVideoCodec)
        && SameText(left.OutputAudioCodec, right.OutputAudioCodec)
        && left.OutputWidth == right.OutputWidth
        && left.OutputHeight == right.OutputHeight
        && left.OutputVideoBitrateBps == right.OutputVideoBitrateBps
        && left.OutputAudioBitrateKbps == right.OutputAudioBitrateKbps
        && left.SelectedAudioStreamIndex == right.SelectedAudioStreamIndex;

    private static bool SameText(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public TranscodeSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;
    public IReadOnlyList<TranscodeSession> ForFile(long fileId) =>
        _sessions.Values.Where(s => s.FileId == fileId && s.IsRunning).ToArray();
    public async Task<bool> StopAsync(string sessionId, string? reason = null)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return false;

        session.Updated -= OnSessionUpdated;
        session.Finished -= OnSessionFinished;
        await session.StopAsync(reason).ConfigureAwait(false);
        session.Dispose();
        return true;
    }
    public async Task<int> StopAllForClientAsync(string? clientId, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return 0;

        var stopped = 0;
        foreach (var session in _sessions.Values)
        {
            if (!string.Equals(session.ClientId, clientId, StringComparison.OrdinalIgnoreCase)) continue;
            if (await StopAsync(session.Id, reason).ConfigureAwait(false)) stopped++;
        }
        return stopped;
    }

    public async Task StopAllAsync(string? reason = null)
    {
        foreach (var id in _sessions.Keys.ToArray())
        {
            await StopAsync(id, reason).ConfigureAwait(false);
        }
    }
    public List<TranscodeSessionDto> Snapshot() =>
        _sessions.Values
            .OrderByDescending(s => s.StartedUtc)
            .Select(s => s.ToDto())
            .ToList();
    private string? _cacheRoot;
    public long MaxCacheBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public static string CachePath(MediaFileRecord file, PlaybackPlan plan, string root)
    {
        var source = new FileInfo(file.Path);
        var identity = System.Text.Json.JsonSerializer.Serialize(new {
            version = 1, path = source.FullName.ToUpperInvariant(), source.Length,
            modified = source.LastWriteTimeUtc.Ticks, plan.VideoAction, plan.AudioOnly,
            plan.OutputWidth, plan.OutputHeight, plan.OutputVideoBitrateBps,
            audio = "all-aac-lc-stereo-192k", video = "h264-yuv420p"
        });
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(root, "completed", key + ".mp4");
    }

    private void PruneCache()
    {
        if (_cacheRoot is null) return;
        var directory = Path.Combine(_cacheRoot, "completed");
        if (!Directory.Exists(directory)) return;
        var files = new DirectoryInfo(directory).GetFiles("*.mp4").OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length);
        var active = _sessions.Values.Select(s => s.CompletedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (total <= MaxCacheBytes) break;
            if (active.Contains(file.FullName)) continue;
            try { var length = file.Length; file.Delete(); total -= length; }
            catch (IOException) { } // An open HTTP range keeps its file alive.
        }
    }

    private readonly List<string> _knownRoots = [];
    public void TrackRoot(string root)
    {
        if (!string.IsNullOrWhiteSpace(root) && !_knownRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            _knownRoots.Add(root);
        }
    }
    public void StartCleanupLoop()
    {
        _cleanupTimer ??= new Timer(_ => _ = CleanupOnceAsync(), null, TimeSpan.FromSeconds(5), CleanupInterval);
        _ = SweepStaleFoldersAsync();
    }
    public async Task CleanupOnceAsync()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var session in _sessions.Values)
        {
            if (session.LastAccessUtc > cutoff) continue;

            var reason = session.IsRunning ? "idle timeout" : "expired";
            _logger.LogInformation("Reaping transcode {SessionId} ({Reason})", session.Id, reason);
            await StopAsync(session.Id, reason).ConfigureAwait(false);
        }
        PruneCache();
    }
    public Task SweepStaleFoldersAsync(CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleFolderAge;
            foreach (var root in _knownRoots.ToArray())
            {
                if (!Directory.Exists(root)) continue;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (ct.IsCancellationRequested) return Task.CompletedTask;
                    try
                    {
                        if (Path.GetFileName(dir) == "completed" || _sessions.Values.Any(s => s.OutputDirectory == dir)) continue;
                        if (Directory.GetCreationTimeUtc(dir) > cutoff) continue;
                        Directory.Delete(dir, recursive: true);
                        _logger.LogDebug("Removed stale transcode folder {Folder}", dir);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stale transcode folder sweep failed");
        }
        return Task.CompletedTask;
    }

    private void OnSessionUpdated(object? sender, TranscodeSession session)
    {
        try
        {
            SessionUpdated?.Invoke(this, session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Transcode progress subscriber threw");
        }
    }

    private void OnSessionFinished(object? sender, TranscodeSession session)
    {
        try
        {
            SessionFinished?.Invoke(this, session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Transcode finished subscriber threw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cleanupTimer is not null)
        {
            await _cleanupTimer.DisposeAsync().ConfigureAwait(false);
            _cleanupTimer = null;
        }

        await StopAllAsync("server shutting down").ConfigureAwait(false);
        _createGate.Dispose();
    }
}

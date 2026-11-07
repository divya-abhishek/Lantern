using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Processes;
using Lantern.Media.Tooling;
using Microsoft.Extensions.Logging;

namespace Lantern.Media.Transcoding;
public sealed class TranscodeSession : IDisposable
{
    private readonly ILogger _logger;
    private readonly StringBuilder _errorTail = new();
    private readonly object _sync = new();
    private Process? _process;
    private Task _completion = Task.CompletedTask;
    private bool _disposed;

    public TranscodeSession(string id, TranscodeRequest request, FfmpegTools tools, ILogger logger)
    {
        Id = id;
        Request = request;
        Tools = tools;
        _logger = logger;
        StartedUtc = DateTime.UtcNow;
        LastAccessUtc = StartedUtc;
    }

    public string Id { get; }
    public TranscodeRequest Request { get; }
    public FfmpegTools Tools { get; }
    public PlaybackPlan Plan => Request.Plan;

    public long FileId => Request.FileId;
    public long MediaId => Request.MediaId;
    public string? ClientId => Request.ClientId;
    public string Title => Request.Title;
    public string SourcePath => Request.SourcePath;
    public string OutputDirectory => Request.OutputDirectory;
    public string CompletedPath => Request.CompletedPath;
    public string PartialPath => Path.Combine(OutputDirectory, "preparing.mp4");
    public bool Ready => State == TranscodeState.Completed && File.Exists(CompletedPath);

    public DateTime StartedUtc { get; }
    public DateTime LastAccessUtc { get; private set; }
    public DateTime? FinishedUtc { get; private set; }

    public TranscodeState State { get; private set; } = TranscodeState.Starting;
    public string? Error { get; private set; }
    public string? Encoder { get; private set; }
    public string? CommandLine { get; private set; }
    public int? ExitCode { get; private set; }

    public TranscodeProgress Progress { get; } = new();
    public long StartMs => Request.StartMs;
    public long? DurationMs => Request.SourceDurationMs;

    public bool IsRunning => State is TranscodeState.Starting or TranscodeState.Running;
    public event EventHandler<TranscodeSession>? Updated;
    public event EventHandler<TranscodeSession>? Finished;
    public double StartSeconds => StartMs / 1000.0;
    public double? Percent
    {
        get
        {
            if (DurationMs is not { } total || total <= 0) return null;
            var done = StartMs + Progress.OutTimeMs;
            var percent = done * 100.0 / total;
            return Math.Clamp(percent, 0, 100);
        }
    }
    public void Start()
    {
        if (File.Exists(CompletedPath))
        {
            State = TranscodeState.Completed;
            FinishedUtc = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(CompletedPath, DateTime.UtcNow);
            return;
        }
        if (!Tools.CanTranscode || Tools.FfmpegPath is null)
        {
            State = TranscodeState.Failed;
            Error = "ffmpeg is not available";
            FinishedUtc = DateTime.UtcNow;
            return;
        }

        var args = TranscodeCommandBuilder.BuildMp4(Request, Tools);
        Encoder = TranscodeCommandBuilder.ResolveEncoder(Request, Tools);
        CommandLine = $"{Tools.FfmpegPath} {TranscodeCommandBuilder.FormatArguments(args)}";

        try
        {
            Directory.CreateDirectory(Request.OutputDirectory);
            _process = ProcessRunner.StartStreaming(Tools.FfmpegPath, args);
        }
        catch (Exception ex)
        {
            State = TranscodeState.Failed;
            Error = ex.Message;
            FinishedUtc = DateTime.UtcNow;
            _logger.LogError(ex, "Could not start transcode {SessionId}", Id);
            return;
        }

        State = TranscodeState.Running;
        _process.ErrorDataReceived += OnErrorData;
        _process.BeginErrorReadLine();
        _completion = WatchExitAsync(_process);

        _logger.LogInformation("Transcode {SessionId} started: {Encoder} {Action} (file {FileId}, offset {Offset}ms)",
            Id, Encoder, Plan.ModeLabel, Request.FileId, Request.StartMs);
    }

    private void OnErrorData(object? sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line)) return;

        var separator = line.IndexOf('=');
        if (separator > 0 && IsProgressKey(line.AsSpan(0, separator).Trim()))
        {
            Progress.Apply(line[..separator].Trim(), line[(separator + 1)..].Trim());
            CountSegmentsIfDue();
            RaiseUpdated();
            return;
        }

        AppendError(line);
    }

    private static bool IsProgressKey(ReadOnlySpan<char> key) => key switch
    {
        "frame" or "fps" or "bitrate" or "total_size" or "out_time_us" or "out_time_ms"
            or "out_time" or "dup_frames" or "drop_frames" or "speed" or "progress"
            or "stream_0_0_q" => true,
        _ => false,
    };

    private void AppendError(string line)
    {
        lock (_sync)
        {
            _errorTail.AppendLine(line);
            if (_errorTail.Length > 4000)
            {
                _errorTail.Remove(0, _errorTail.Length - 2000);
            }
        }
        _logger.LogWarning("ffmpeg[{SessionId}]: {Line}", Id, line);
    }

    private void CountSegmentsIfDue()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastSegmentScan) < TimeSpan.TicksPerSecond) return;
        Interlocked.Exchange(ref _lastSegmentScan, now);
        ScanOutput();
    }
    private void ScanOutput()
    {
        Progress.SetMeasuredOutput(OutputBytes(), Progress.OutTimeMs);
        Progress.SetSegments(CountSegments());
    }
    public int CountSegments() => 0;
    public long OutputBytes()
    {
        try { return new FileInfo(Ready ? CompletedPath : PartialPath).Length; }
        catch (IOException) { return 0; }
    }

    private async Task WatchExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Waiting for transcode {SessionId} failed", Id);
        }

        int exit;
        try
        {
            exit = process.ExitCode;
        }
        catch
        {
            exit = -1;
        }

        lock (_sync)
        {
            ExitCode = exit;
            ScanOutput();
            if (State is TranscodeState.Stopped)
            {
            }
            else if (exit == 0 && File.Exists(PartialPath) && new FileInfo(PartialPath).Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CompletedPath)!);
                    // Same-volume rename publishes only a fully closed, faststart MP4.
                    try { File.Move(PartialPath, CompletedPath); }
                    catch (IOException) when (File.Exists(CompletedPath))
                    {
                        // Another client finished the same immutable cache entry.
                        File.Delete(PartialPath);
                    }
                    State = TranscodeState.Completed;
                }
                catch (IOException ex) { State = TranscodeState.Failed; Error = ex.Message; }
            }
            else
            {
                State = TranscodeState.Failed;
                var tail = _errorTail.ToString().Trim();
                Error = tail.Length > 0
                    ? tail
                    : $"ffmpeg produced no playable stream (exit code {exit})";
            }

            FinishedUtc = DateTime.UtcNow;
        }

        if (State == TranscodeState.Failed)
        {
            _logger.LogError("Transcode {SessionId} failed: {Error}", Id, Error);
        }
        else
        {
            _logger.LogInformation("Transcode {SessionId} finished ({State}, {Segments} segments)",
                Id, State, Progress.SegmentCount);
        }

        RaiseUpdated();
        Finished?.Invoke(this, this);
    }

    private void RaiseUpdated()
    {
        try
        {
            Updated?.Invoke(this, this);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Transcode progress subscriber threw");
        }
    }
    public void Touch()
    {
        LastAccessUtc = DateTime.UtcNow;
        if (Ready) File.SetLastWriteTimeUtc(CompletedPath, LastAccessUtc);
    }
    private long _lastSegmentScan;

    public async Task StopAsync(string? reason = null)
    {
        Process? process;
        var notify = false;

        lock (_sync)
        {
            if (State is TranscodeState.Stopped or TranscodeState.Completed or TranscodeState.Failed)
            {
                process = null;
            }
            else
            {
            State = TranscodeState.Stopped;
            Error ??= reason;
            FinishedUtc = DateTime.UtcNow;
            process = _process;
            notify = true;
            }
        }

        if (process is not null)
        {
            ProcessRunner.TryKill(process, _logger);
        }

        // Do not delete/dispose the work directory or process while its completion
        // handler is still publishing an output or consuming stderr.
        await _completion.ConfigureAwait(false);
        if (notify) _logger.LogInformation("Transcode {SessionId} stopped ({Reason})", Id, reason ?? "by request");
    }
    public void CleanupOutput()
    {
        try
        {
            if (Directory.Exists(OutputDirectory))
            {
                Directory.Delete(OutputDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete transcode folder {Folder}", OutputDirectory);
        }
    }
    public TranscodeSessionDto ToDto() => new()
    {
        SessionId = Id,
        MediaId = MediaId,
        Title = Title.Length > 0 ? Title : Path.GetFileName(SourcePath),
        Mode = Plan.Mode,
        VideoAction = Plan.VideoAction,
        AudioAction = Plan.AudioAction,
        StartSeconds = StartSeconds,
        StartedUtc = StartedUtc,
        OutputBytes = Progress.TotalSizeBytes,
        Speed = Progress.Speed,
        Fps = Progress.Fps > 0 ? Progress.Fps : null,
        OutputBitrateBps = Progress.BitrateBps > 0 ? Progress.BitrateBps : null,
        Running = IsRunning,
        LastError = Error,
        LastAccessUtc = LastAccessUtc,
    };
    public string Describe()
    {
        var percent = Percent;
        var parts = new List<string> { Plan.ModeLabel };
        if (Encoder is not null) parts.Add(Encoder);
        if (percent is not null) parts.Add($"{percent.Value:0}%");
        parts.Add($"{Progress.Speed:0.0}x");
        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var process = _process;
        if (process is not null)
        {
            if (State is TranscodeState.Starting or TranscodeState.Running)
            {
                State = TranscodeState.Stopped;
            }
            ProcessRunner.TryKill(process, _logger);
            try
            {
                process.ErrorDataReceived -= OnErrorData;
                process.Dispose();
            }
            catch
            {
            }
        }

        CleanupOutput();
    }
}

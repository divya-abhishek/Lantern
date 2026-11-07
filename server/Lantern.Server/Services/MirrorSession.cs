namespace Lantern.Server.Services;

using System.Diagnostics;
using System.Globalization;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Mirroring;
using Lantern.Media.Processes;
using Microsoft.Extensions.Logging;

/// <summary>
/// One live screen mirroring session: it owns the capture/encode process and the RTSP relay the
/// picture is published into, and it reports what it can actually measure about the result.
///
/// Why the receiver queue is gone: the old session read MPEG-TS off ffmpeg's stdout, scanned it for
/// program tables and keyframes, then kept a bounded queue *per receiver* and dropped stale data to
/// hold latency down. All of that was this server doing a relay's job, and the parts it got wrong were
/// the parts that showed on the projector. Now ffmpeg publishes H.264 + AAC to MediaMTX over RTSP/TCP
/// and the relay serves every receiver from the live edge; this class starts, watches and reports on
/// that pair, and no longer touches a single frame of the stream.
///
/// The measurements it keeps are all real, and they are deliberately separated:
///  - <see cref="MirrorStatusDto.LatencyMs"/> is capture-to-publish - ffmpeg's own output timestamp
///    against the wall clock the picture is stamped with - which is the delay this PC contributes;
///  - <see cref="MirrorStatusDto.DriftMs"/> is how much that changed since the first frame, which is
///    the accumulation check the old design could not pass;
///  - <see cref="MirrorStatusDto.Subscribers"/> and
///    <see cref="MirrorStatusDto.ReaderDiscardedPackets"/> come from the relay's own API, so a receiver
///    that cannot keep up is visible as a number instead of a guess.
/// </summary>
public sealed class MirrorSession : IAsyncDisposable
{
    private readonly MirrorCapturePlan _plan;
    private readonly MirrorSettingsDto _settings;
    private readonly string _ffmpegPath;
    private readonly string _relayExecutable;
    private readonly string _bindAddress;
    private readonly string _advertisedAddress;
    private readonly string _sourceLabel;
    private readonly ILogger _logger;
    private readonly SystemAudioCapture? _audio;
    private readonly object _errorGate = new();
    private readonly Queue<string> _recentErrors = new();

    private Process? _process;
    private MediaMtxRelay? _relay;
    private CancellationTokenSource? _lifetime;
    private Task? _progress;
    private bool _audioRunning;
    private int _audioStarted;

    // --- measurements -----------------------------------------------------------------------------

    /// <summary>
    /// The wall clock the picture's timestamps are measured from: the instant the audio capture began,
    /// which is the origin both streams are stamped against (see MirrorCommandBuilder.VideoClock).
    /// Timestamps are microseconds since that instant, so ffmpeg's newest output timestamp subtracted
    /// from the current clock is the pipeline's own delay - one clock, both ends, no model.
    /// </summary>
    private DateTime _originUtc;

    private long _videoFrames;
    private double _measuredFps;
    private long _newestOutTimeMicros = -1;
    private int _latencyMs;
    private int _firstLatencyMs = -1;
    private int _driftMs;
    private long _publishedBytes;
    private long _readerBytes;
    private long _sampledPublishedBytes;
    private long _sampledReaderBytes;
    private long _sampledFrames;
    private DateTime _windowStartUtc = DateTime.UtcNow;
    private int _measuredBitrateKbps;
    private int _relayReaders;
    private long _relayDiscarded;

    public MirrorSession(
        MirrorCapturePlan plan,
        MirrorSettingsDto settings,
        string sourceLabel,
        string ffmpegPath,
        string relayExecutable,
        string bindAddress,
        string advertisedAddress,
        ILogger logger,
        SystemAudioCapture? audio = null)
    {
        _plan = plan;
        _settings = settings;
        _sourceLabel = sourceLabel;
        _ffmpegPath = ffmpegPath;
        _relayExecutable = relayExecutable;
        _bindAddress = bindAddress;
        _advertisedAddress = advertisedAddress;
        _logger = logger;
        _audio = audio;
        StartedUtc = DateTime.UtcNow;
    }

    public DateTime StartedUtc { get; }

    public MirrorState State { get; private set; } = MirrorState.Idle;

    public string? LastError { get; private set; }

    public MirrorCapturePlan Plan => _plan;

    public MirrorSettingsDto Settings => _settings;

    public string SourceLabel => _sourceLabel;

    public bool IsRunning => State is MirrorState.Starting or MirrorState.Live;

    /// <summary>True once the WASAPI loopback capture is actually attached and feeding ffmpeg.</summary>
    public bool AudioActive => _audioRunning && _audio?.Failure is null;

    /// <summary>The relay this session publishes through, once it is up.</summary>
    public MediaMtxRelay? Relay => _relay;

    /// <summary>
    /// Brings up the relay, then the encoder, and only calls the session live once the relay has the
    /// picture. The order matters: ffmpeg's RTSP publish fails outright if nothing is listening, so the
    /// relay is started first and its listener is verified by connecting to it.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is not MirrorState.Idle)
        {
            throw new InvalidOperationException("The session was already started.");
        }

        State = MirrorState.Starting;
        _lifetime = new CancellationTokenSource();

        try
        {
            _relay = await MediaMtxRelay
                .StartAsync(
                    new MediaMtxRelayOptions
                    {
                        ExecutablePath = _relayExecutable,
                        BindAddress = _bindAddress,
                        AdvertisedAddress = _advertisedAddress,
                    },
                    _logger,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            State = MirrorState.Failed;
            LastError = ex.Message;
            throw new InvalidOperationException(LastError, ex);
        }

        _logger.LogInformation(
            "Screen mirror relay listening on {Address}:{Port} for path {Path}",
            string.IsNullOrWhiteSpace(_relay.BindAddress) ? "*" : _relay.BindAddress,
            _relay.Port,
            _relay.SessionPath);

        // Where the sound begins is where this session's timeline begins: the loopback capture is
        // started first and ffmpeg is told the same instant, so the picture is stamped against the
        // sound's origin instead of against the moment the encoder finished coming up. That single
        // shared origin is the whole A/V alignment story - see MirrorCommandBuilder.VideoClock - and it
        // is also what lets the status measure capture-to-publish with ffmpeg's own timestamps.
        _originUtc = DateTime.UtcNow;
        _audio?.BeginCapture();

        var args = MirrorCommandBuilder.Build(_plan, _relay.PublishUrl, _audio?.CaptureStartedMicros ?? 0);
        _logger.LogInformation("Screen mirror starting: {Args}", MirrorCommandBuilder.FormatArguments(args));

        try
        {
            _process = StartProcess(args);
        }
        catch (Exception ex)
        {
            await StopRelayAsync().ConfigureAwait(false);
            State = MirrorState.Failed;
            LastError = $"ffmpeg could not be started: {ex.Message}";
            throw new InvalidOperationException(LastError, ex);
        }

        // The captured sound now has somewhere to go. Nothing is lost in between: the samples wait in
        // the capture's queue from the instant capture began.
        if (_audio is not null) AttachAudio();

        _windowStartUtc = DateTime.UtcNow;
        _progress = Task.Run(() => ReadProgressAsync(_lifetime.Token));

        // ffmpeg can still refuse the capture (no desktop access, a busy GPU). Give it a moment and fail
        // here rather than let the UI claim the stream is live.
        if (_process.WaitForExit(900))
        {
            await StopRelayAsync().ConfigureAwait(false);
            State = MirrorState.Failed;
            LastError = DescribeExit(_process.ExitCode);
            throw new InvalidOperationException(LastError);
        }

        // Then wait for the relay to actually hold the stream before calling the session live.
        //
        // This is not politeness: a receiver that opens the URL in the second before ffmpeg announces
        // gets an RTSP 404 from the relay, and the projector would start its retry loop against a stream
        // that was one moment from existing. Measured on this machine, the announce lands 1-3 s after
        // ffmpeg starts - exactly the window a device that polled hard would fall into.
        var announced = await WaitForStreamAsync(ct).ConfigureAwait(false);

        if (!announced)
        {
            var reason = _process.HasExited
                ? DescribeExit(_process.ExitCode)
                : "The relay never received a stream from the encoder.";

            await StopRelayAsync().ConfigureAwait(false);
            State = MirrorState.Failed;
            LastError = reason;
            throw new InvalidOperationException(reason);
        }

        State = MirrorState.Live;
    }

    /// <summary>
    /// Waits until the relay reports this session's path online with a video track.
    ///
    /// Reads the relay's own view of the stream rather than trusting the encoder: the relay knows what it
    /// has received, and the path turning online is the exact moment a receiver's DESCRIBE will succeed.
    /// </summary>
    private async Task<bool> WaitForStreamAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_process is null || _process.HasExited) return false;

            var stats = await _relay!.StatsAsync(ct).ConfigureAwait(false);
            if (stats is { Online: true, HasVideo: true }) return true;

            try
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private Process StartProcess(List<string> args)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            // stdout carries ffmpeg's progress report (`-progress pipe:1`); stderr carries its log.
            // Neither is a media stream any more - the picture goes to the relay over the socket RTSP
            // opened - so both are read and neither can block the encoder.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = _plan.Audio,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            // At the boundary it is useful to have ffmpeg's own words: this is where its warnings and its
            // failure text arrive, and with verbose logging on it is also where `-debug_ts` and filter
            // reports show what the pipeline is actually doing.
            _logger.LogDebug("ffmpeg: {Line}", MirrorCommandBuilder.Redact(e.Data));
            RememberError(MirrorCommandBuilder.Redact(e.Data));
        };

        process.Start();
        Lantern.Core.ProcessOwnership.Attach(process);
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Hands the captured sound to ffmpeg's stdin; failure only costs the sound.
    ///
    /// Called once, immediately after the process is started. The capture itself began a moment
    /// earlier (see <see cref="StartAsync"/>) so that the origin is the sound's, not the encoder's.
    /// </summary>
    private void AttachAudio()
    {
        try
        {
            if (_audio is null || _process is null) return;

            _audio.AttachTo(_process.StandardInput.BaseStream);
            _audioRunning = Interlocked.Exchange(ref _audioStarted, 1) == 0;

            if (_audioRunning)
            {
                _logger.LogInformation(
                    "Screen mirror audio: system audio from {Device} at {Rate} Hz / {Channels} ch",
                    _audio.DeviceName, _audio.SampleRate, _audio.Channels);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System audio capture could not be started");
        }
    }

    // --- the measurements -------------------------------------------------------------------------

    /// <summary>
    /// Reads ffmpeg's progress report. Every block ends with <c>progress=continue</c> and carries the
    /// encoder's own counters, so the numbers the status shows are the encoder's rather than this
    /// server's estimate of them.
    ///
    /// Nothing here blocks on a receiver: with RTSP there is no receiver in this process at all.
    /// </summary>
    private async Task ReadProgressAsync(CancellationToken ct)
    {
        if (_process is null) return;

        var reader = _process.StandardOutput;
        var line = new System.Text.StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var next = reader.Read();
                if (next < 0) break;

                if (next is '\n' or '\r')
                {
                    if (line.Length > 0)
                    {
                        ApplyProgress(line.ToString());
                        line.Clear();
                    }

                    continue;
                }

                line.Append((char)next);
            }
        }
        catch (OperationCanceledException)
        {
            // A stopping session cancels the read on purpose.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The mirror progress reader stopped");
        }
    }

    /// <summary>One <c>key=value</c> line of ffmpeg's progress report.</summary>
    private void ApplyProgress(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0) return;

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "frame":
                if (long.TryParse(value, CultureInfo.InvariantCulture, out var frames)) _videoFrames = frames;
                break;

            case "fps":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)
                    && fps > 0)
                {
                    _measuredFps = fps;
                }

                break;

            // `out_time_us` is the timestamp of the newest packet the muxer has written, in
            // microseconds on the session's own timeline. `out_time_ms` is the same number under a name
            // that lied for years (it has always been microseconds); it is read as a fallback so that an
            // older ffmpeg still measures.
            case "out_time_us":
            case "out_time_ms":
                if (long.TryParse(value, CultureInfo.InvariantCulture, out var micros)) TrackLatency(micros);
                break;
        }
    }

    /// <summary>
    /// Turns the newest output timestamp into a latency reading. The stream's timestamps are stamped
    /// from <see cref="_originUtc"/>, so "what the newest packet says the time is" minus "what the time
    /// actually is" is the delay the whole capture-encode-mux-publish chain is holding. Both ends come
    /// from the same wall clock, so this is a measurement rather than a model.
    /// </summary>
    private void TrackLatency(long outTimeMicros)
    {
        if (outTimeMicros < 0) return;

        _newestOutTimeMicros = outTimeMicros;

        var age = (DateTime.UtcNow - _originUtc).TotalMilliseconds - outTimeMicros / 1000.0;

        // A reading outside this band is a clock that has not settled yet (or a filtergraph that has not
        // been configured), not a latency worth showing.
        if (age is < -2_000 or > 60_000) return;

        var smoothed = _latencyMs == 0
            ? (int)Math.Round(age)
            : (int)Math.Round(_latencyMs * 0.8 + age * 0.2);

        _latencyMs = smoothed;

        if (_firstLatencyMs < 0)
        {
            _firstLatencyMs = smoothed;
            _driftMs = 0;
            return;
        }

        // Drift is the *change*: a pipeline running at a constant delay is fine however large that delay
        // is, while one whose delay keeps growing is the failure this design exists to prevent.
        _driftMs = smoothed - _firstLatencyMs;
    }

    /// <summary>
    /// Folds the relay's numbers into the status and recomputes the one second rate window.
    ///
    /// Called from the status endpoint rather than from a timer, so a session nobody is watching costs
    /// nothing and every number the caller sees was read a moment before it was sent.
    /// </summary>
    private async Task RefreshFromRelayAsync(CancellationToken ct)
    {
        var relay = _relay;
        if (relay is null) return;

        var stats = await relay.StatsAsync(ct).ConfigureAwait(false);

        if (stats is null)
        {
            // The relay is gone (killed, or crashed): the session is over even if ffmpeg has not noticed
            // yet, and saying so beats reporting numbers from a dead socket.
            if (State is MirrorState.Live or MirrorState.Starting && !relay.IsRunning)
            {
                State = MirrorState.Failed;
                LastError = "The screen mirror relay stopped. Start mirroring again.";
                _logger.LogWarning("Screen mirror relay stopped; the session is over ({Path})", relay.SessionPath);
            }

            return;
        }

        _relayReaders = stats.Readers;
        _relayDiscarded = stats.ReaderDiscardedPackets;
        _publishedBytes = stats.BytesReceived;
        _readerBytes = stats.BytesSent;

        var now = DateTime.UtcNow;
        var elapsed = (now - _windowStartUtc).TotalSeconds;
        if (elapsed < 1) return;

        // Deltas of monotonic totals: immune to the reset-per-window bookkeeping this kind of counter is
        // prone to, and it cannot count the same byte twice.
        var published = stats.BytesReceived - _sampledPublishedBytes;
        var sent = stats.BytesSent - _sampledReaderBytes;
        var frames = _videoFrames - _sampledFrames;

        // "Live bitrate" has always meant what leaves the server towards receivers, so that is what is
        // reported while anybody is watching. With nobody attached the bytes going into the relay are
        // still the picture's real rate - they are exactly what a receiver would be given - so the
        // publisher's rate is the fallback rather than a zero.
        var movingBytes = stats.Readers > 0 ? sent : published;
        _measuredBitrateKbps = (int)Math.Round(Math.Max(0, movingBytes) * 8 / elapsed / 1000);

        if (frames > 0) _measuredFps = Math.Round(frames / elapsed, 1);

        // One line every few seconds with the raw ingredients of the latency reading. The number the UI
        // shows is a difference of two clocks, and when it says something surprising this is what says
        // which side of it moved: ffmpeg's own output timestamp, the wall clock it is measured against,
        // and how many seconds of sound the loopback device has actually handed over in that time.
        LogMeasurement(now);

        _sampledPublishedBytes = stats.BytesReceived;
        _sampledReaderBytes = stats.BytesSent;
        _sampledFrames = _videoFrames;
        _windowStartUtc = now;
    }

    private DateTime _lastMeasurementLogUtc = DateTime.MinValue;

    private void LogMeasurement(DateTime now)
    {
        if ((now - _lastMeasurementLogUtc).TotalSeconds < 5) return;

        _lastMeasurementLogUtc = now;

        var elapsedMs = (now - _originUtc).TotalMilliseconds;
        var newestMs = _newestOutTimeMicros / 1000.0;
        var audioSeconds = _audio is null
            ? -1
            : (double)_audio.CapturedBytes / (_audio.SampleRate * _audio.Channels * 2);

        _logger.LogDebug(
            "mirror measurement: elapsed={Elapsed}ms newestOutTime={Newest}ms latency={Latency}ms " +
            "drift={Drift}ms frames={Frames} audioSeconds={Audio:0.00} audioSilence={Silence}ms audioLag={Lag}ms",
            (long)elapsedMs, (long)newestMs, _latencyMs, _driftMs, _videoFrames,
            audioSeconds, _audio?.SilenceMs ?? 0, _audio?.ContentLagMs ?? 0);
    }

    // --- reporting and teardown --------------------------------------------------------------------

    /// <summary>
    /// The live status, with the relay's numbers refreshed from the relay itself.
    ///
    /// The RTSP URL is included only while the session is running: it is this session's credential, and
    /// handing it out after the stream stops would be handing out a URL that is about to become a dud.
    /// <paramref name="advertisedHost"/> is the address the asking device should dial.
    /// </summary>
    public async Task<MirrorStatusDto> BuildStatusAsync(string? advertisedHost = null, CancellationToken ct = default)
    {
        await RefreshFromRelayAsync(ct).ConfigureAwait(false);

        var relay = _relay;
        var live = State is MirrorState.Live;

        return new MirrorStatusDto
        {
            Active = IsRunning,
            State = State,
            Settings = _settings,
            SourceLabel = _sourceLabel,
            Encoder = _plan.Encoder,
            CaptureBackend = _plan.CaptureBackend,
            HardwareEncoding = _plan.HardwareEncoding,
            Audio = _plan.Audio && AudioActive,
            AudioDevice = AudioActive ? _audio?.DeviceName : null,
            AudioSilenceMs = _audio?.SilenceMs ?? 0,
            AudioDroppedBytes = _audio?.DroppedBytes ?? 0,
            AudioLagMs = _audio?.ContentLagMs ?? 0,
            TargetBitrateKbps = _plan.BitrateKbps,
            MeasuredBitrateKbps = _measuredBitrateKbps,
            FramesPerSecond = _measuredFps,
            LatencyMs = _latencyMs,
            DriftMs = _driftMs,
            BufferedMs = 0,
            Subscribers = _relayReaders,
            ReaderDiscardedPackets = _relayDiscarded,
            FramesSent = _videoFrames,
            // Keyframes and dropped bytes were counted from the MPEG-TS bytes this server used to scan.
            // It does not see the packets any more, and inventing numbers for them would be worse than
            // reporting nothing: the relay's own per-receiver discard counter is reported instead.
            KeyframesSeen = 0,
            DroppedBytes = 0,
            TotalBytes = _publishedBytes,
            StartedUtc = StartedUtc,
            UptimeSeconds = (long)(DateTime.UtcNow - StartedUtc).TotalSeconds,
            LastError = _audio?.Failure is { } audioFailure
                ? $"System audio capture failed. Check the Windows playback device and restart mirroring. {MirrorCommandBuilder.Redact(audioFailure)}"
                : LastError,
            StreamUrl = live ? relay?.ReadUrlFor(advertisedHost) : null,
            Transport = "rtsp",
            RelayPath = relay?.SessionPath,
        };
    }

    /// <summary>ffmpeg's own exit, in words the UI can show.</summary>
    private string DescribeExit(int exitCode)
    {
        var tail = LastErrorLine();

        var reason = exitCode switch
        {
            0 => "ffmpeg stopped before it published anything.",
            _ => $"ffmpeg exited with code {exitCode}.",
        };

        return tail is null ? reason : $"{reason} {tail}";
    }

    private void RememberError(string line)
    {
        // Only the last few lines before a failure explain anything; the rest is routine progress noise.
        lock (_errorGate)
        {
            _recentErrors.Enqueue(line);
            while (_recentErrors.Count > 12) _recentErrors.Dequeue();
        }
    }

    private string? LastErrorLine()
    {
        lock (_errorGate)
        {
            return _recentErrors.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        }
    }

    private async Task StopRelayAsync()
    {
        var relay = _relay;
        _relay = null;
        if (relay is null) return;

        try
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The screen mirror relay could not be stopped cleanly");
        }
    }

    /// <summary>Stops the capture and releases ffmpeg, the relay and the audio device.</summary>
    public async ValueTask DisposeAsync()
    {
        if (State != MirrorState.Failed) State = MirrorState.Stopping;

        try
        {
            _lifetime?.Cancel();
        }
        catch (Exception)
        {
            // A cancelled session may already have been disposed.
        }

        if (_progress is not null)
        {
            try
            {
                await _progress.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A cancelled or faulted reader is expected during teardown.
            }

            _progress = null;
        }

        if (_process is not null) ProcessRunner.TryKill(_process, _logger);

        _audio?.Dispose();
        _audioRunning = false;

        await StopRelayAsync().ConfigureAwait(false);

        _lifetime?.Dispose();
        _lifetime = null;

        if (State != MirrorState.Failed) State = MirrorState.Idle;
    }
}

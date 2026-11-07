using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Desktop.Mvvm;
using Lantern.Desktop.Services;

namespace Lantern.Desktop.ViewModels;

/// <summary>A display the server can capture, as the Screen Mirror page presents it.</summary>
public sealed record MirrorDisplayOption(int Index, string Name, bool Primary, int Width, int Height)
{
    public string Label =>
        $"Display {Index + 1} · {Width} × {Height}{(Primary ? " · Primary" : string.Empty)}";
}

/// <summary>
/// Screen Mirror: the settings the PC sends, the capability report the server measured, and the
/// live numbers while a stream is running.
/// </summary>
public sealed partial class LanternViewModel
{
    private string _mirrorResolution = "720";
    private string _mirrorFps = "30";
    private string _mirrorFrameRateNote =
        "Checking available frame rates…";
    private string _mirrorQuality = "medium";
    private bool _mirrorAudio = true;
    private string _mirrorStatus = "Checking what this PC can capture…";
    private string _mirrorEncoderSummary = "detecting…";
    private string _mirrorCaptureBackend = "detecting…";
    private string _mirrorAudioDevices = "detecting…";
    private string _mirrorLiveResolution = "—";
    private string _mirrorLiveFps = "—";
    private string _mirrorLiveBitrate = "—";
    private string _mirrorLiveLatency = "—";
    private string _mirrorLiveReceivers = "—";
    private string _mirrorLiveUptime = "—";
    private bool _mirrorReady;
    private bool _isMirroring;
    private bool _capabilitiesLoaded;
    private MirrorDisplayOption? _selectedDisplay;
    private readonly SemaphoreSlim _mirrorOperation = new(1, 1);
    private int _mirrorSettingsVersion;
    private bool _mirrorRestartPending;

    public ObservableCollection<MirrorDisplayOption> Displays { get; } = [];

    public MirrorDisplayOption? SelectedDisplay
    {
        get => _selectedDisplay;
        set { if (Set(ref _selectedDisplay, value) && (_isMirroring || _mirrorRestartPending)) _ = RestartMirrorAsync(); }
    }

    /// <summary>
    /// The resolution the encoder is asked for, and the pills that choose it.
    ///
    /// The pills are bound to these properties rather than to their own visual state, because a pill
    /// that reads "1280 × 720" while the running session encodes 1920 × 1080 is worse than having no
    /// selector at all - and that mismatch is what "720p is selected but the session says 1080p" was.
    /// They are also the only place the choice is remembered: the XAML used to hard-code the 1280 × 720
    /// pill as checked and set the value from a Checked handler, which fires while the view is still
    /// being constructed and can therefore be swallowed, leaving the picture and the panel disagreeing.
    /// </summary>
    public string MirrorResolution
    {
        get => _mirrorResolution;
        set => SetMirrorSetting(ref _mirrorResolution, value, nameof(MirrorResolution),
            nameof(MirrorAt720), nameof(MirrorAt1080));
    }

    public bool MirrorAt720
    {
        get => _mirrorResolution == "720";
        set { if (value) MirrorResolution = "720"; }
    }

    public bool MirrorAt1080
    {
        get => _mirrorResolution == "1080";
        set { if (value) MirrorResolution = "1080"; }
    }

    /// <summary>
    /// The frame rate the capture is asked for, and the pills that choose it: "30" or "60".
    ///
    /// This was hard-coded to 30 in three places - the pills, the request this view model builds, and the
    /// read-back from the running session - so the selector could not move off 30 no matter what the
    /// server accepted, and the whole pipeline (capture rate, the fps filter, the keyframe interval, the
    /// declared H.264 level and the bitrate) was 30 fps with no way to ask for anything else. The value
    /// here is the one the server is sent; the server snaps it onto its own ladder and reports back what
    /// it resolved.
    /// </summary>
    public string MirrorFps
    {
        get => _mirrorFps;
        set => SetMirrorSetting(ref _mirrorFps, value, nameof(MirrorFps),
            nameof(MirrorAt30), nameof(MirrorAt60));
    }

    public bool MirrorAt30
    {
        get => _mirrorFps == "30";
        set { if (value) MirrorFps = "30"; }
    }

    public bool MirrorAt60
    {
        get => _mirrorFps == "60";
        set { if (value) MirrorFps = "60"; }
    }

    /// <summary>
    /// What the server measured about the top of the ladder on this machine.
    ///
    /// Replaces a caption that claimed 30 was "the only rate offered": 60 is offered, and this line says
    /// whether the selected encoder actually encoded 1920 × 1080 @ 60 here as a test - the encoder's own
    /// words when it refused, rather than a reassurance. The live panel below is what the running session
    /// really delivered, so the two together are measurement from both ends.
    /// </summary>
    public string MirrorFrameRateNote
    {
        get => _mirrorFrameRateNote;
        private set => Set(ref _mirrorFrameRateNote, value);
    }

    public string MirrorQuality
    {
        get => _mirrorQuality;
        set => SetMirrorSetting(ref _mirrorQuality, value, nameof(MirrorQuality),
            nameof(MirrorQualityLow), nameof(MirrorQualityMedium), nameof(MirrorQualityHigh));
    }

    public bool MirrorQualityLow
    {
        get => _mirrorQuality == "low";
        set { if (value) MirrorQuality = "low"; }
    }

    public bool MirrorQualityMedium
    {
        get => _mirrorQuality == "medium";
        set { if (value) MirrorQuality = "medium"; }
    }

    public bool MirrorQualityHigh
    {
        get => _mirrorQuality == "high";
        set { if (value) MirrorQuality = "high"; }
    }

    /// <summary>
    /// Stores one encoder setting and applies it to a session that is already running by starting that
    /// session again with the new value.
    ///
    /// A running ffmpeg has already been told what to do, so there were three options: ignore the
    /// change while streaming (and let the panel claim a resolution the stream does not carry), stop
    /// the capture and leave the projector dark until somebody presses Start, or restart. Restarting is
    /// the only one where what the panel says is what the projector receives, and it is what the
    /// status line reports while it happens.
    /// </summary>
    private void SetMirrorSetting(ref string field, string value, string property, params string[] dependents)
    {
        if (!Set(ref field, value, property)) return;

        foreach (var name in dependents) Raise(name);

        if (_isMirroring || _mirrorRestartPending) _ = RestartMirrorAsync();
    }

    /// <summary>The resolution the settings ask for right now, for status messages.</summary>
    private string MirrorLiveResolutionLabel =>
        _mirrorResolution == "1080" ? "1920 × 1080" : "1280 × 720";

    /// <summary>
    /// The selected rate as the number the request carries.
    ///
    /// Anything the panel cannot produce is sent as 30 - the same snapping the server does on its own
    /// ladder - so a state the UI has no way to reach cannot become a guess on the wire.
    /// </summary>
    private int MirrorFpsValue => _mirrorFps == "60" ? 60 : 30;

    /// <summary>Re-starts the running capture so a settings change actually reaches the encoder.</summary>
    private async Task RestartMirrorAsync()
    {
        var version = ++_mirrorSettingsVersion;
        _mirrorRestartPending = true;
        try
        {
            await Task.Delay(350, _lifetime.Token);
            if (version != _mirrorSettingsVersion) return;
            MirrorStatus = $"Restarting at {MirrorLiveResolutionLabel}…";
            await StartMirrorAsync(version);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReportError($"Mirroring could not be restarted with the new settings. {ex.Message}");
        }
        finally { if (version == _mirrorSettingsVersion) _mirrorRestartPending = false; }
    }

    public bool MirrorAudioEnabled
    {
        get => _mirrorAudio;
        set { if (Set(ref _mirrorAudio, value) && (_isMirroring || _mirrorRestartPending)) _ = RestartMirrorAsync(); }
    }

    public string MirrorStatus
    {
        get => _mirrorStatus;
        private set => Set(ref _mirrorStatus, value);
    }

    public string MirrorEncoderSummary
    {
        get => _mirrorEncoderSummary;
        private set => Set(ref _mirrorEncoderSummary, value);
    }

    public string MirrorCaptureBackend
    {
        get => _mirrorCaptureBackend;
        private set => Set(ref _mirrorCaptureBackend, value);
    }

    public string MirrorAudioDevices
    {
        get => _mirrorAudioDevices;
        private set => Set(ref _mirrorAudioDevices, value);
    }

    /// <summary>
    /// True when this PC can actually mirror: connected to a server, with a display, a capture
    /// backend and an encoder that the server verified by encoding with it.
    /// </summary>
    public bool CanMirror => IsConnected && _mirrorReady && !_isMirroring;

    /// <summary>True while the server is capturing and streaming this PC's screen.</summary>
    public bool IsMirroring
    {
        get => _isMirroring;
        private set
        {
            if (!Set(ref _isMirroring, value)) return;

            Raise(nameof(CanMirror));
            Raise(nameof(CanStopMirror));
            Raise(nameof(MirrorStateLabel));
        }
    }

    public bool CanStopMirror => _isMirroring;

    public string MirrorStateLabel => _isMirroring ? "● LIVE" : "idle";

    public string MirrorLiveResolution
    {
        get => _mirrorLiveResolution;
        private set => Set(ref _mirrorLiveResolution, value);
    }

    public string MirrorLiveFps
    {
        get => _mirrorLiveFps;
        private set => Set(ref _mirrorLiveFps, value);
    }

    public string MirrorLiveBitrate
    {
        get => _mirrorLiveBitrate;
        private set => Set(ref _mirrorLiveBitrate, value);
    }

    public string MirrorLiveLatency
    {
        get => _mirrorLiveLatency;
        private set => Set(ref _mirrorLiveLatency, value);
    }

    public string MirrorLiveReceivers
    {
        get => _mirrorLiveReceivers;
        private set => Set(ref _mirrorLiveReceivers, value);
    }

    public string MirrorLiveUptime
    {
        get => _mirrorLiveUptime;
        private set => Set(ref _mirrorLiveUptime, value);
    }

    public ICommand StartMirrorCommand { get; private set; } = null!;
    public ICommand StopMirrorCommand { get; private set; } = null!;

    private void InitMirror()
    {
        StartMirrorCommand = new AsyncRelayCommand(StartMirrorAsync);
        StopMirrorCommand = new AsyncRelayCommand(StopMirrorAsync);

        // One loop does both jobs: it loads the capability report once a server is attached and then
        // keeps the live numbers fresh, including when mirroring was started from the projector
        // side rather than from this window.
        _ = PollMirrorAsync();
    }

    /// <summary>Loads the capability report the server measured, and keeps the live numbers fresh.</summary>
    private async Task PollMirrorAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                if (IsConnected)
                {
                    if (!_capabilitiesLoaded)
                    {
                        ApplyCapabilities(await _client
                            .GetMirrorCapabilitiesAsync(refresh: true, _lifetime.Token)
                            .ConfigureAwait(true));
                        _capabilitiesLoaded = true;
                    }

                    ApplyStatus(await _client.GetMirrorStatusAsync(_lifetime.Token).ConfigureAwait(true));
                }
            }
            catch (Exception)
            {
                // A poll is allowed to fail: the server may be restarting, or the page is simply not
                // open. The next tick tries again rather than reporting an error the user cannot act on.
            }

            try
            {
                await Task.Delay(1500, _lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>Fills the page from the server's measured capability report.</summary>
    private void ApplyCapabilities(MirrorCapabilitiesDto capabilities)
    {
        Displays.Clear();
        foreach (var display in capabilities.Displays)
        {
            Displays.Add(new MirrorDisplayOption(
                display.Index, display.Name, display.Primary, display.Width, display.Height));
        }

        if (SelectedDisplay is null) SelectedDisplay = Displays.FirstOrDefault();

        MirrorCaptureBackend = capabilities.CaptureBackend switch
        {
            "ddagrab" => "Windows Desktop Duplication (ddagrab)",
            "gdigrab" => "GDI screen capture (gdigrab)",
            _ => "not available",
        };

        MirrorEncoderSummary = capabilities.Encoders.Count == 0
            ? "no H.264 encoder passed a test encode"
            : string.Join(" → ", capabilities.Encoders) +
              (capabilities.HardwareEncoding ? " (hardware in use)" : " (software)");

        MirrorAudioDevices = capabilities.AudioSupported
            ? $"system audio · {capabilities.AudioDevice ?? "default playback device"}"
            : capabilities.AudioReason ?? "no loopback device available";

        _mirrorReady = capabilities.Ready;
        Raise(nameof(CanMirror));

        MirrorFrameRateNote = FrameRateNote(capabilities);

        MirrorStatus = capabilities.Ready
            ? "Ready to share your screen."
            : !capabilities.FfmpegAvailable
                ? "Run Set up Lantern from the extracted folder to install the streaming tools, then reopen Lantern."
                : capabilities.UnsupportedReason ?? "This PC cannot capture its screen.";
    }

    private static string FrameRateNote(MirrorCapabilitiesDto capabilities) =>
        capabilities.VerifiedMaxFrameRate >= 60
            ? "Use 60 FPS for smoother motion on a capable receiver."
            : "Start with 30 FPS. If you try 60 FPS, start at 720p.";

    /// <summary>Mirrors the live session into the page (and into the button state).</summary>
    private void ApplyStatus(MirrorStatusDto status)
    {
        IsMirroring = status.Active;

        if (!status.Active)
        {
            MirrorLiveResolution = "—";
            MirrorLiveFps = "—";
            MirrorLiveBitrate = "—";
            MirrorLiveLatency = "—";
            MirrorLiveReceivers = "—";
            MirrorLiveUptime = "—";

            if (status.LastError is { Length: > 0 } error) MirrorStatus = error;
            return;
        }

        var settings = status.Settings;
        MirrorLiveResolution = settings is null ? "—" : $"{settings.Width} × {settings.Height}";

        // The panel follows the running session. The server is the authority on what is being
        // encoded, so if the two ever disagree the pills move to match the stream. The backing fields
        // are written directly rather than through the properties: those restart a live session, and a
        // status poll that restarted the capture would be an endless loop of restarts.
        if (!_mirrorRestartPending) SyncSettingsFromSession(settings);

        var targetFps = settings?.Fps ?? 0;

        // The measured rate next to the one that was asked for, and a word when they disagree: this is
        // the line that answers "is 60 FPS really running", from the encoder's own output rather than
        // from the request.
        MirrorLiveFps = $"{status.FramesPerSecond:0.#} fps (target {targetFps})" +
                        (targetFps > 0 && status.FramesPerSecond < targetFps * 0.8 ? " — below target" : string.Empty);
        MirrorLiveBitrate = $"{status.MeasuredBitrateKbps / 1000.0:0.0} Mbps (target {status.TargetBitrateKbps / 1000.0:0.0})";

        // Two different numbers, and the pair is the useful one: the publisher's own lag, as ffmpeg
        // measures it (real, but a property of this PC's pipeline - and with the sound attached it carries
        // a constant multi-second offset that is documented in MirrorStatusDto.LatencyMs), and how much
        // that lag has moved since the first frame, which is the accumulation check this whole transport
        // change exists to pass.
        MirrorLiveLatency = status.LatencyMs <= 0
            ? "measuring…"
            : $"{status.LatencyMs} ms publisher clock estimate (drift {status.DriftMs:+#;-#;0} ms)";

        MirrorLiveReceivers = status.Subscribers == 1 ? "1 receiver" : $"{status.Subscribers} receivers";
        MirrorLiveUptime = TimeSpan.FromSeconds(status.UptimeSeconds).ToString(@"mm\:ss");

        // The capture target and the encoded size are two different things - a 1920 × 1080 desktop
        // can be sent as 1280 × 720 - and this line used to print only the first one, which reads
        // exactly like a claim about the second. Both are named now, and the size that is actually
        // being encoded comes from the session rather than from the selector, so it cannot flatter it.
        // The rate is part of where the stream is going rather than a detail: "1920 × 1080 @ 60" is what
        // was asked for, and the rate measured back from the encoder sits in the panel above.
        var target = settings is null ? MirrorLiveResolution : $"{MirrorLiveResolution} @ {settings.Fps}";

        MirrorStatus = status.Subscribers == 0
            ? "Live. Open Screen Mirror on the receiver and select Start receiving."
            : $"Streaming to {MirrorLiveReceivers.ToLowerInvariant()} at {target}.";
    }

    /// <summary>
    /// Mirrors a running session's own settings back into the selector, without going through the
    /// properties (which would restart the session and poll-loop). When nothing is running the
    /// selector is left as the viewer left it, so the next Start uses what is on screen.
    /// </summary>
    private void SyncSettingsFromSession(MirrorSettingsDto? settings)
    {
        if (settings is not { Width: > 0, Height: > 0 }) return;

        var resolution = settings.Width >= 1920 ? "1080" : "720";

        // The rate the session is really running at, read back off the session rather than assumed: the
        // server resolves what it was asked for, so this is the answer. A session started from another
        // client - the projector, an older build, a hand-written request - moves the pills to match here
        // instead of leaving the panel showing a rate that nothing is running at.
        var fps = settings.Fps >= 50 ? "60" : "30";
        var quality = settings.Quality switch
        {
            StreamQuality.Low => "low",
            StreamQuality.High or StreamQuality.Original => "high",
            _ => "medium",
        };

        if (_mirrorResolution != resolution)
        {
            _mirrorResolution = resolution;
            Raise(nameof(MirrorResolution));
            Raise(nameof(MirrorAt720));
            Raise(nameof(MirrorAt1080));
        }

        if (_mirrorFps != fps)
        {
            _mirrorFps = fps;
            Raise(nameof(MirrorFps));
            Raise(nameof(MirrorAt30));
            Raise(nameof(MirrorAt60));
        }

        if (_mirrorQuality != quality)
        {
            _mirrorQuality = quality;
            Raise(nameof(MirrorQuality));
            Raise(nameof(MirrorQualityLow));
            Raise(nameof(MirrorQualityMedium));
            Raise(nameof(MirrorQualityHigh));
        }
    }

    /// <summary>Asks the server to start capturing this PC's screen.</summary>
    private Task StartMirrorAsync() => StartMirrorAsync(null);

    private async Task StartMirrorAsync(int? settingsVersion)
    {
        if (!IsConnected) return;

        await _mirrorOperation.WaitAsync(_lifetime.Token);
        try
        {
            if (settingsVersion.HasValue && settingsVersion != _mirrorSettingsVersion) return;
            ClearError();

            // With several monitors the whole desktop needs GDI capture, so "Entire desktop" is
            // offered as such; a single monitor is the same thing either way.
            var useDisplay = SelectedDisplay is not null && Displays.Count > 1;

            var settings = new MirrorSettingsDto
            {
                Source = useDisplay ? MirrorSource.Display : MirrorSource.Desktop,
                DisplayIndex = SelectedDisplay?.Index ?? 0,
                Width = _mirrorResolution == "1080" ? 1920 : 1280,
                Height = _mirrorResolution == "1080" ? 1080 : 720,
                // The selected rate, not a constant. This is the one line that decides everything
                // downstream - the capture's own framerate, the fps filter that re-grids the frames, the
                // keyframe interval, the declared H.264 level and the target bitrate - and it used to be
                // the literal 30, which is why a 60 FPS pill would have changed nothing on its own.
                Fps = MirrorFpsValue,
                Quality = ParseMirrorQuality(_mirrorQuality),
                Audio = _mirrorAudio,
            };

            ApplyStatus(await _client.StartMirrorAsync(settings, _lifetime.Token).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            ReportError($"Mirroring could not start. {ex.Message}");
        }
        finally { _mirrorOperation.Release(); }
    }

    private async Task StopMirrorAsync()
    {
        ++_mirrorSettingsVersion;
        _mirrorRestartPending = false;
        await _mirrorOperation.WaitAsync(_lifetime.Token);
        try
        {
            await _client.StopMirrorAsync(_lifetime.Token).ConfigureAwait(true);
            MirrorStatus = "Stopped.";
            ApplyStatus(new MirrorStatusDto());
        }
        catch (Exception ex)
        {
            ReportError($"Mirroring could not be stopped. {ex.Message}");
        }
        finally { _mirrorOperation.Release(); }
    }

    private static StreamQuality ParseMirrorQuality(string value) => value switch
    {
        "low" => StreamQuality.Low,
        "high" => StreamQuality.High,
        _ => StreamQuality.Medium,
    };

    // --- playing on this PC -------------------------------------------------------------

    /// <summary>
    /// Opens the stream the planner resolved with the system's default media handler. For a local
    /// movie file this is the direct-play byte stream, which is what Media Cast is built around.
    /// </summary>
    private async Task PlayAsync(MediaCard? card, bool resume)
    {
        var target = card ?? SelectedCard;
        if (target is null || !IsConnected || string.IsNullOrWhiteSpace(ServerUrl)) return;

        try
        {
            var detail = await _client.GetMediaAsync(target.Id, plan: true, _lifetime.Token).ConfigureAwait(true);
            var relative = await PreparePlayableUrlAsync(target.Id);

            if (relative is null)
            {
                ReportError("The server did not provide a play URL for this title.");
                return;
            }

            var absolute = new Uri(new Uri(ServerUrl.TrimEnd('/') + "/"), relative.TrimStart('/')).ToString();
            var position = resume ? detail?.PositionMs ?? 0 : 0;

            Process.Start(new ProcessStartInfo(absolute) { UseShellExecute = true });

            StatusMessage = position > 0
                ? $"Opening on this PC · resume point {Core.Dtos.MediaSummaryDto.FormatTime(position)}"
                : "Opening on this PC";

            await _client
                .ReportProgressAsync(target.Id, position, detail?.DurationMs ?? 0, playing: true, _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError($"Playback could not be started. {ex.Message}");
        }
    }

    private async Task<string?> PreparePlayableUrlAsync(long mediaId)
    {
        var plan = await _client.PlanAsync(mediaId, StreamQuality.Auto, _lifetime.Token);
        while (string.Equals(plan.PreparationState, "Preparing", StringComparison.OrdinalIgnoreCase)) {
            StatusMessage = $"Preparing on this PC · {plan.PreparationPercent ?? 0:0}%";
            await Task.Delay(1000, _lifetime.Token);
            plan = await _client.PlanAsync(mediaId, StreamQuality.Auto, _lifetime.Token);
        }
        if (string.Equals(plan.PreparationState, "Failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Media preparation failed. Check the server diagnostics.");
        return plan.PlayUrl;
    }

    /// <summary>Puts the resolved stream URL on the clipboard (handy for VLC or a phone).</summary>
    private async Task CopyLinkAsync(MediaCard? card)
    {
        var target = card ?? SelectedCard;
        if (target is null || !IsConnected || string.IsNullOrWhiteSpace(ServerUrl)) return;

        try
        {
            var detail = await _client.GetMediaAsync(target.Id, plan: true, _lifetime.Token).ConfigureAwait(true);
            var relative = await PreparePlayableUrlAsync(target.Id);
            if (relative is null) return;

            var absolute = new Uri(new Uri(ServerUrl.TrimEnd('/') + "/"), relative.TrimStart('/')).ToString();
            Clipboard.SetText(absolute);
            StatusMessage = "Stream link copied to the clipboard.";
        }
        catch (Exception ex)
        {
            ReportError($"The link could not be copied. {ex.Message}");
        }
    }

    // --- shared plumbing ---------------------------------------------------------------

    /// <summary>Shows a failure without stopping the app.</summary>
    public void ReportError(string message)
    {
        ErrorMessage = message;
        StatusMessage = message;
    }

    public void ClearError() => ErrorMessage = string.Empty;

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception)
        {
            // Cancelling an already finished token is fine.
        }

        _host.Dispose();
        _client.Dispose();
        _lifetime.Dispose();
    }
}

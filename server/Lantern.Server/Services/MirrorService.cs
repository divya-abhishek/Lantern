namespace Lantern.Server.Services;

using System.Net;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Mirroring;
using Lantern.Media.Tooling;
using Microsoft.Extensions.Logging;

/// <summary>
/// Owns the one screen mirroring session a PC can run, plus the measured capability report.
///
/// There is exactly one capture per machine on purpose: a second ffmpeg grabbing the same desktop
/// costs another full encode and helps nobody. Receivers, on the other hand, are unlimited - they
/// all attach to the same session through the RTSP relay, which is what makes "projector plus a
/// laptop watching" work without a second encoder.
/// </summary>
public sealed class MirrorService : IAsyncDisposable
{
    private readonly FfmpegLocator _locator;
    private readonly MirrorCapabilityProbe _probe;
    private readonly ServerRuntime _runtime;
    private readonly ILogger<MirrorService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MirrorSession? _session;
    private MirrorCapabilitiesDto? _capabilities;
    private string? _lastError;

    public MirrorService(
        FfmpegLocator locator,
        MirrorCapabilityProbe probe,
        ServerRuntime runtime,
        ILogger<MirrorService> logger)
    {
        _locator = locator;
        _probe = probe;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// What this PC can mirror. The probe runs a real test encode per candidate encoder, which
    /// costs a couple of seconds once, so the result is cached until explicitly refreshed.
    /// </summary>
    public async Task<MirrorCapabilitiesDto> CapabilitiesAsync(
        bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _capabilities is not null) return _capabilities;

        var tools = await _locator
            .ResolveAsync(_runtime.SettingsNow, refresh, ct)
            .ConfigureAwait(false);

        _runtime.Tools = tools;

        var capabilities = await _probe
            .ProbeAsync(tools, SystemAudioCapture.Availability(), ct)
            .ConfigureAwait(false);

        capabilities = await AttachRelayAsync(capabilities, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Screen mirror capability: backend={Backend} encoder={Encoder} displays={Displays} audio={Audio} relay={Relay}",
            capabilities.CaptureBackend ?? "none",
            capabilities.SelectedEncoder ?? "none",
            capabilities.Displays.Count,
            capabilities.AudioSupported ? capabilities.AudioDevice ?? "yes" : capabilities.AudioReason ?? "no",
            capabilities.RelayAvailable ? capabilities.RelayVersion ?? "available" : capabilities.RelayReason ?? "missing");

        _capabilities = capabilities;
        return capabilities;
    }

    /// <summary>
    /// Adds the relay to the report. The media layer does not know about MediaMTX - it is a tool on this
    /// machine, like ffmpeg - so it is resolved here, where the settings and the logger already live, and
    /// cached with the rest of the report.
    ///
    /// A missing relay is not treated as a footnote: mirroring cannot work without it, which is why it
    /// feeds <see cref="MirrorCapabilitiesDto.Ready"/> and why <see cref="StartAsync"/> refuses to start
    /// with the reason from here.
    /// </summary>
    private async Task<MirrorCapabilitiesDto> AttachRelayAsync(
        MirrorCapabilitiesDto capabilities, CancellationToken ct)
    {
        var path = MediaMtxLocator.Find(_runtime.SettingsNow.MediaMtxPath, out var reason);

        if (path is null)
        {
            capabilities.RelayAvailable = false;
            capabilities.RelayReason = reason;
            return capabilities;
        }

        capabilities.RelayPath = path;
        capabilities.RelayAvailable = true;
        capabilities.RelayVersion = await MediaMtxLocator
            .VersionAsync(path, _logger, ct)
            .ConfigureAwait(false);

        if (capabilities.RelayVersion is null)
        {
            // Found but not runnable is a different failure from not found, and it is worth saying so
            // before a session tries to start it.
            capabilities.RelayAvailable = false;
            capabilities.RelayReason = $"The relay at {path} could not be run.";
        }

        return capabilities;
    }

    /// <summary>
    /// Live state, or an idle report that still explains the last failure. The caller's own address is
    /// passed through so the RTSP URL it receives is one it can actually reach.
    /// </summary>
    public async Task<MirrorStatusDto> StatusAsync(
        IPAddress? remoteAddress = null, CancellationToken ct = default)
    {
        var session = _session;
        if (session is not null)
        {
            return await session
                .BuildStatusAsync(_runtime.PrimaryAddressFor(remoteAddress), ct)
                .ConfigureAwait(false);
        }

        return new MirrorStatusDto
        {
            Active = false,
            State = _lastError is null ? MirrorState.Idle : MirrorState.Failed,
            LastError = _lastError,
        };
    }

    /// <summary>The running session, if any. The stream endpoint attaches receivers to it.</summary>
    public MirrorSession? Current => _session;

    /// <summary>Starts a session, replacing one that is already running. Returns the failure reason, or null.</summary>
    public async Task<string?> StartAsync(MirrorSettingsDto settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastError = null;

            // Asking to mirror while already mirroring means "restart with these settings".
            await StopInternalAsync().ConfigureAwait(false);

            var capabilities = await CapabilitiesAsync(refresh: false, ct).ConfigureAwait(false);

            if (!capabilities.PlatformSupported
                || !capabilities.FfmpegAvailable
                || capabilities.SelectedEncoder is null)
            {
                return Record(capabilities.UnsupportedReason ?? "This PC cannot capture its screen.");
            }

            if (capabilities.Displays.Count == 0)
            {
                return Record("Windows reports no display that could be captured.");
            }

            // The relay is the transport: without it there is nothing to publish into, and a session
            // that starts anyway would only fail one step later with a worse message.
            var relayExecutable = MediaMtxLocator.Find(_runtime.SettingsNow.MediaMtxPath, out var relayReason);
            if (relayExecutable is null)
            {
                return Record(relayReason);
            }

            var displayIndex = Math.Clamp(settings.DisplayIndex, 0, capabilities.Displays.Count - 1);

            var plan = MirrorProfiles.Resolve(
                settings.Source,
                displayIndex,
                settings.Width,
                settings.Height,
                settings.Fps,
                settings.Quality,
                audio: false,
                capabilities.SelectedEncoder,
                MirrorProfiles.PickCaptureBackend(
                    capabilities.CaptureBackends, settings.Source, capabilities.Displays.Count),
                capabilities.Displays.Count);

            var (planWithAudio, audioCapture) = AttachAudio(plan, settings, capabilities);

            // The relay listens on the machine's LAN address and nowhere else: the mirror exists to reach
            // a projector on this network, and a listener that also answers on any other interface would
            // be a way in that nobody asked for. When there is no LAN address to use it falls back to
            // every interface rather than refusing to mirror.
            var bindAddress = _runtime.LanAddresses().FirstOrDefault() ?? string.Empty;

            var session = new MirrorSession(
                planWithAudio,
                settings,
                Describe(settings, capabilities),
                capabilities.FfmpegPath!,
                relayExecutable,
                bindAddress,
                bindAddress,
                _logger,
                audioCapture);

            try
            {
                await session.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return Record(ex.Message);
            }

            _session = session;

            _logger.LogInformation(
                "Screen mirroring live: {Source} {Width}x{Height}@{Fps} {Quality} {Encoder} {Bitrate} kbps audio={Audio}",
                session.SourceLabel, planWithAudio.Width, planWithAudio.Height, planWithAudio.Fps,
                MirrorProfiles.QualityLabel(planWithAudio.Quality), planWithAudio.Encoder,
                planWithAudio.BitrateKbps, session.AudioActive);

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens WASAPI loopback when the user asked for sound, so the plan can carry the device's real
    /// PCM format. If the device cannot be opened the session still starts - muted, and the reason
    /// is in the log - because the picture is the point.
    /// </summary>
    private (MirrorCapturePlan Plan, SystemAudioCapture? Audio) AttachAudio(
        MirrorCapturePlan plan, MirrorSettingsDto settings, MirrorCapabilitiesDto capabilities)
    {
        if (!settings.Audio) return (plan, null);

        if (!capabilities.AudioSupported)
        {
            _logger.LogWarning(
                "Mirroring without audio: {Reason}",
                capabilities.AudioReason ?? "Windows system audio cannot be captured on this machine");
            return (plan, null);
        }

        var (capture, reason) = SystemAudioCapture.TryCreate();
        if (capture is null)
        {
            _logger.LogWarning("Mirroring without audio: {Reason}", reason);
            return (plan, null);
        }

        return (plan with
        {
            Audio = true,
            AudioSampleRate = capture.SampleRate,
            AudioChannels = capture.Channels,
        }, capture);
    }

    /// <summary>Human readable capture target for the UI, e.g. "Display 1 · 1920 × 1080 · Primary".</summary>
    private static string Describe(MirrorSettingsDto settings, MirrorCapabilitiesDto capabilities)
    {
        var displays = capabilities.Displays;

        if (settings.Source == MirrorSource.Display && displays.Count > 0)
        {
            var index = Math.Clamp(settings.DisplayIndex, 0, displays.Count - 1);
            var display = displays[index];
            var primary = display.Primary ? " · Primary" : string.Empty;
            return $"Display {index + 1} · {display.Width} × {display.Height}{primary}";
        }

        return displays.Count == 1
            ? $"Entire desktop · {displays[0].Width} × {displays[0].Height}"
            : $"Entire desktop · {displays.Count} displays";
    }

    /// <summary>Stops the capture. Safe to call when nothing is running.</summary>
    public async Task<bool> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var stopped = _session is not null || _lastError is not null;
            await StopInternalAsync().ConfigureAwait(false);
            _lastError = null;
            return stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Screen mirroring stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The mirror session could not be stopped cleanly");
        }
    }

    private string Record(string error)
    {
        _lastError = error;
        _logger.LogWarning("Screen mirroring could not start: {Error}", error);
        return error;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}


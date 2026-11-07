namespace Lantern.Media.Mirroring;

using System.Runtime.InteropServices;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Processes;
using Lantern.Media.Tooling;
using Microsoft.Extensions.Logging;

/// <summary>Result of asking the host whether Windows system audio can be captured.</summary>
public sealed record MirrorAudioAvailability(bool Supported, string? Device, string? Reason);

/// <summary>
/// Measures what this PC can actually do for screen mirroring, instead of assuming.
///
/// The encoder check is deliberately stronger than "ask ffmpeg what it supports": an encoder is
/// only reported when it successfully encodes a few frames with the exact flag set the session will
/// use. A name in <c>ffmpeg -encoders</c> proves nothing - this machine lists h264_amf and h264_qsv
/// yet has no AMD GPU, and h264_nvenc is listed even where the driver refuses to initialise it.
/// </summary>
public sealed class MirrorCapabilityProbe
{
    private readonly ILogger<MirrorCapabilityProbe> _logger;

    public MirrorCapabilityProbe(ILogger<MirrorCapabilityProbe> logger) => _logger = logger;

    /// <summary>
    /// Probes the machine. <paramref name="audio"/> is supplied by the host - it is the only part
    /// that needs a Windows audio API - so the media layer stays free of platform audio code.
    /// </summary>
    public async Task<MirrorCapabilitiesDto> ProbeAsync(
        FfmpegTools tools,
        MirrorAudioAvailability? audio = null,
        CancellationToken ct = default)
    {
        var displays = Displays();

        var capabilities = new MirrorCapabilitiesDto
        {
            PlatformSupported = OperatingSystem.IsWindows(),
            FfmpegAvailable = tools.Available && tools.CanTranscode,
            FfmpegPath = tools.FfmpegPath,
            Displays = displays,
            AudioSupported = audio?.Supported ?? false,
            AudioDevice = audio?.Device,
            AudioReason = audio?.Reason,
        };

        if (!capabilities.PlatformSupported)
        {
            capabilities.UnsupportedReason =
                "Screen mirroring captures a Windows desktop, and this server is running elsewhere.";
            return capabilities;
        }

        if (!capabilities.FfmpegAvailable)
        {
            capabilities.UnsupportedReason =
                "ffmpeg was not found, so nothing can be captured. Install it " +
                "(winget install Gyan.FFmpeg) or point Lantern at it in Settings.";
            return capabilities;
        }

        capabilities.CaptureBackends = await CaptureBackendsAsync(tools.FfmpegPath!, ct).ConfigureAwait(false);
        capabilities.CaptureBackend = MirrorProfiles.PickCaptureBackend(
            capabilities.CaptureBackends, MirrorSource.Desktop, displays.Count);

        if (capabilities.CaptureBackends.Count == 0)
        {
            capabilities.UnsupportedReason =
                "This ffmpeg build has neither the ddagrab nor the gdigrab capture backend.";
            return capabilities;
        }

        capabilities.Encoders = await VerifiedEncodersAsync(tools.FfmpegPath!, ct).ConfigureAwait(false);
        capabilities.SelectedEncoder = MirrorProfiles.PickEncoder(capabilities.Encoders);
        capabilities.HardwareEncoding = capabilities.SelectedEncoder is not null
                                        && MirrorProfiles.IsHardware(capabilities.SelectedEncoder);

        if (capabilities.SelectedEncoder is null)
        {
            capabilities.UnsupportedReason = "No H.264 encoder on this machine accepted a test encode.";
            return capabilities;
        }

        // The ladder the clients offer comes from here rather than from a constant written into each
        // client: the two have to agree, and the default is the ladder's own first step instead of the
        // same number spelled out twice.
        capabilities.FrameRates = [.. MirrorProfiles.FrameRates];
        capabilities.DefaultFps = MirrorProfiles.FrameRates[0];
        (capabilities.DefaultWidth, capabilities.DefaultHeight) = MirrorProfiles.Resolutions[0];

        // Only now can the top of the ladder be attempted: it is the *selected* encoder that has to do it.
        await VerifyTopFrameRateAsync(tools.FfmpegPath!, capabilities, ct).ConfigureAwait(false);

        return capabilities;
    }

    /// <summary>
    /// Measures the top of the ladder - 1920 × 1080 at 60 - with the encoder the session will use.
    ///
    /// 1080p60 is 489,600 macroblocks a second, exactly double level 4.1's 245,760, so it is a different
    /// encoder job rather than a scaled-up 720p30 one: the declared level changes (4.2), the bitrate
    /// doubles, and a driver that accepts one can refuse the other outright. One test encode on a
    /// synthetic frame is what turns "60 is offered" into "60 was encoded on this machine", and when it
    /// fails it is the encoder's own error that is reported, because that is what a fix is written
    /// against.
    ///
    /// A failure does not remove 60 from the ladder. The probe covers the largest target, and a smaller
    /// one (720p60) may well work; the running session's own measured rate is the authority once it is
    /// live. The result is reported - and the UI says which case it is - rather than the offer being
    /// quietly withdrawn or silently promised.
    /// </summary>
    private async Task VerifyTopFrameRateAsync(
        string ffmpeg, MirrorCapabilitiesDto capabilities, CancellationToken ct)
    {
        var (width, height) = MirrorProfiles.Resolutions[^1];
        var fps = MirrorProfiles.FrameRates[^1];
        var quality = StreamQuality.High;

        capabilities.FrameRateProbeLabel = $"{width} × {height} @ {fps}";

        var plan = new MirrorCapturePlan
        {
            Source = MirrorSource.Desktop,
            DisplayIndex = 0,
            Width = width,
            Height = height,
            Fps = fps,
            Quality = quality,
            Encoder = capabilities.SelectedEncoder!,
            CaptureBackend = "gdigrab",
            Audio = false,
            BitrateBps = MirrorProfiles.BitrateBps(width, height, fps, quality),
            GopFrames = MirrorProfiles.GopFor(fps),
        };

        var result = await ProcessRunner
            .RunAsync(ffmpeg, MirrorCommandBuilder.BuildEncoderProbe(plan), TimeSpan.FromSeconds(30), _logger, ct)
            .ConfigureAwait(false);

        if (result.Success)
        {
            capabilities.VerifiedMaxFrameRate = fps;
            capabilities.FrameRateReason =
                $"{capabilities.FrameRateProbeLabel} encoded with {plan.Encoder} " +
                $"({MirrorProfiles.LevelFor(height, fps)} level, {plan.BitrateKbps} kbps) as a test.";
            _logger.LogDebug(
                "Mirror encoder {Encoder} verified at {Label}", plan.Encoder, capabilities.FrameRateProbeLabel);
            return;
        }

        // 30 is the rate this encoder already encoded in the probe above, so it is the measured fallback
        // rather than a guess about what the hardware can do.
        capabilities.VerifiedMaxFrameRate = MirrorProfiles.FrameRates[0];
        capabilities.FrameRateReason =
            $"{capabilities.FrameRateProbeLabel} was refused by {plan.Encoder}: {LastLine(result.Combined)}";
        _logger.LogInformation(
            "Mirror frame rate {Fps} was not verified on this machine: {Error}",
            fps, LastLine(result.Combined));
    }

    /// <summary>Capture backends this ffmpeg build exposes, best first.</summary>
    private async Task<List<string>> CaptureBackendsAsync(string ffmpeg, CancellationToken ct)
    {
        // ddagrab is a filter (it is a source filter), gdigrab is an input device.
        var filters = await ProcessRunner
            .RunAsync(ffmpeg, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(20), _logger, ct)
            .ConfigureAwait(false);

        var devices = await ProcessRunner
            .RunAsync(ffmpeg, ["-hide_banner", "-devices"], TimeSpan.FromSeconds(20), _logger, ct)
            .ConfigureAwait(false);

        var found = new List<string>();
        foreach (var candidate in MirrorProfiles.CaptureBackendPriority)
        {
            var haystack = candidate == "ddagrab" ? filters.Combined : devices.Combined;
            if (haystack.Contains(candidate, StringComparison.OrdinalIgnoreCase)) found.Add(candidate);
        }

        return found;
    }

    /// <summary>Encoders that survived a real encode with the session's own flag set.</summary>
    private async Task<List<string>> VerifiedEncodersAsync(string ffmpeg, CancellationToken ct)
    {
        var verified = new List<string>();

        foreach (var encoder in MirrorProfiles.EncoderPriority)
        {
            ct.ThrowIfCancellationRequested();

            // 720p30 / Medium is the default session, so the probe validates exactly that shape.
            var quality = StreamQuality.Medium;
            var plan = new MirrorCapturePlan
            {
                Source = MirrorSource.Desktop,
                DisplayIndex = 0,
                Width = 1280,
                Height = 720,
                Fps = 30,
                Quality = quality,
                Encoder = encoder,
                CaptureBackend = "gdigrab",
                Audio = false,
                BitrateBps = MirrorProfiles.BitrateBps(1280, 720, 30, quality),
                GopFrames = MirrorProfiles.GopFor(30),
            };

            var args = MirrorCommandBuilder.BuildEncoderProbe(plan);
            var result = await ProcessRunner
                .RunAsync(ffmpeg, args, TimeSpan.FromSeconds(30), _logger, ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                verified.Add(encoder);
                _logger.LogDebug("Mirror encoder {Encoder} verified by test encode", encoder);
            }
            else
            {
                _logger.LogDebug(
                    "Mirror encoder {Encoder} rejected: {Error}", encoder, LastLine(result.Combined));
            }
        }

        return verified;
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

    // --- Windows monitor enumeration ------------------------------------------------------

    /// <summary>Attached displays as Windows reports them, primary first.</summary>
    public static List<MirrorDisplayDto> Displays()
    {
        var list = new List<MirrorDisplayDto>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            var order = 0;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };

                if (GetMonitorInfo(monitor, ref info))
                {
                    list.Add(new MirrorDisplayDto
                    {
                        Index = order++,
                        Name = (info.Device ?? string.Empty).TrimStart('\\', '.'),
                        Primary = (info.Flags & MonitorInfoPrimary) != 0,
                        Width = info.Monitor.Right - info.Monitor.Left,
                        Height = info.Monitor.Bottom - info.Monitor.Top,
                    });
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Enumeration failing means no displays are reported, which the UI already explains.
        }

        return list.OrderByDescending(d => d.Primary).ThenBy(d => d.Index).ToList();
    }

    private const uint MonitorInfoPrimary = 0x00000001;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Device;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}


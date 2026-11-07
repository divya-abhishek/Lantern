namespace Lantern.Media.Mirroring;

using Lantern.Core.Models;

/// <summary>
/// A validated, resolved capture plan: everything the ffmpeg command line needs, decided in one
/// place so the ladder can be reasoned about (and measured) instead of being scattered through
/// the builder.
/// </summary>
public sealed record MirrorCapturePlan
{
    public required MirrorSource Source { get; init; }

    /// <summary>Display index when <see cref="Source"/> is <see cref="MirrorSource.Display"/>.</summary>
    public required int DisplayIndex { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Fps { get; init; }

    public required StreamQuality Quality { get; init; }

    /// <summary>The ffmpeg encoder name that will be used (already verified to work).</summary>
    public required string Encoder { get; init; }

    /// <summary><c>ddagrab</c> (GPU / Desktop Duplication) or <c>gdigrab</c> (GDI, whole desktop).</summary>
    public required string CaptureBackend { get; init; }

    /// <summary>Capture system audio through WASAPI loopback and mux it as AAC.</summary>
    public required bool Audio { get; init; }

    /// <summary>Sample rate of the PCM pushed into ffmpeg's stdin (the capture device's own rate).</summary>
    public int AudioSampleRate { get; init; } = MirrorCommandBuilder.AudioSampleRate;

    /// <summary>Channel count of that PCM.</summary>
    public int AudioChannels { get; init; } = MirrorCommandBuilder.AudioChannels;

    public required int BitrateBps { get; init; }

    /// <summary>Keyframe interval in frames: half a second, so a receiver joining mid stream waits at most that long.</summary>
    public required int GopFrames { get; init; }

    public bool HardwareEncoding => MirrorProfiles.IsHardware(Encoder);

    public int BitrateKbps => BitrateBps / 1000;
}

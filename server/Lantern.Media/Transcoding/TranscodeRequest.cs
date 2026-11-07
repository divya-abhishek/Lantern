using Lantern.Core.Models;

namespace Lantern.Media.Transcoding;
public sealed class TranscodeRequest
{
    public required string SourcePath { get; init; }
    public required string OutputDirectory { get; init; }
    public required string CompletedPath { get; init; }

    public required PlaybackPlan Plan { get; init; }
    public long StartMs { get; init; }

    public long? SourceDurationMs { get; init; }
    public double? SourceFrameRate { get; init; }
    public int? SourceAudioChannels { get; init; }
    public int? SelectedAudioOrdinal { get; init; }
    public int SegmentSeconds { get; init; } = 4;
    public int ListSize { get; init; }
    public string EncoderPreference { get; init; } = "auto";
    public string? ClientId { get; init; }

    public long FileId { get; init; }
    public long MediaId { get; init; }
    public string Title { get; init; } = string.Empty;

    public bool CopyVideo => Plan.VideoAction.Equals("copy", StringComparison.OrdinalIgnoreCase);
    public bool CopyAudio => Plan.AudioAction.Equals("copy", StringComparison.OrdinalIgnoreCase);
    public bool AudioOnly => Plan.AudioOnly;
}

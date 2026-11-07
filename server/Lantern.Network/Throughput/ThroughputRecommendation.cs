namespace Lantern.Network.Throughput;

/// <summary>
/// Turns a measured LAN rate into a playback decision: either the file can be copied as-is
/// (direct play) or it must be transcoded down to <see cref="SuggestedBitrateKbps"/>.
/// </summary>
/// <remarks>
/// The rule is the one every LAN player uses: keep ~30% of the measured bandwidth for audio,
/// container overhead and Wi-Fi jitter, then pick the highest rung of the quality ladder that
/// still fits. The ladder itself only contains bitrates the server can actually encode.
/// </remarks>
public sealed class ThroughputRecommendation
{
    /// <summary>Fraction of the measured bandwidth that may be used by the video stream.</summary>
    public const double HeadroomFactor = 0.7;

    /// <summary>Rate above which a normal 1080p remux streams without transcoding.</summary>
    public const int DirectPlayThresholdKbps = 12_000;

    /// <summary>Highest first: what the server offers when the link cannot take the original file.</summary>
    public static readonly IReadOnlyList<QualityStep> Ladder =
    [
        new(12_000, "1080p high", "1080p at 12 Mbps"),
        new(8_000, "1080p", "1080p at 8 Mbps"),
        new(6_000, "1080p lite", "1080p at 6 Mbps"),
        new(4_000, "720p", "720p at 4 Mbps"),
        new(3_000, "720p lite", "720p at 3 Mbps"),
        new(2_000, "480p", "480p at 2 Mbps"),
        new(1_200, "480p lite", "480p at 1.2 Mbps"),
        new(800, "360p", "360p at 800 kbps"),
        new(500, "360p lite", "360p at 500 kbps"),
    ];

    /// <summary>The lowest rung; also the answer for an unusable measurement.</summary>
    public static QualityStep Fallback => Ladder[^1];

    /// <summary>True when the original file can be streamed untouched.</summary>
    public bool DirectPlayPossible { get; init; }

    /// <summary>Bitrate the transcoder should target; null for direct play.</summary>
    public int? SuggestedBitrateKbps { get; init; }

    /// <summary>Human readable rung of the ladder, shown in the quality picker.</summary>
    public string Quality { get; init; } = Fallback.Label;

    /// <summary>The measured rate reduced by <see cref="HeadroomFactor"/>.</summary>
    public double HeadroomKbps { get; init; }

    /// <summary>Recommendation for a measured rate in megabits per second.</summary>
    public static ThroughputRecommendation For(double megabitsPerSecond)
    {
        if (double.IsNaN(megabitsPerSecond) || megabitsPerSecond <= 0)
        {
            return new ThroughputRecommendation
            {
                DirectPlayPossible = false,
                SuggestedBitrateKbps = Fallback.BitrateKbps,
                Quality = Fallback.Label,
                HeadroomKbps = 0,
            };
        }

        var headroom = megabitsPerSecond * 1000 * HeadroomFactor;

        if (headroom >= DirectPlayThresholdKbps)
        {
            return new ThroughputRecommendation
            {
                DirectPlayPossible = true,
                SuggestedBitrateKbps = null,
                Quality = "direct play (original quality)",
                HeadroomKbps = headroom,
            };
        }

        var step = Fallback;
        foreach (var candidate in Ladder)
        {
            if (candidate.BitrateKbps <= headroom)
            {
                step = candidate;
                break;
            }
        }

        return new ThroughputRecommendation
        {
            DirectPlayPossible = false,
            SuggestedBitrateKbps = step.BitrateKbps,
            Quality = step.Label,
            HeadroomKbps = headroom,
        };
    }

    public override string ToString() =>
        DirectPlayPossible
            ? $"direct play (headroom {HeadroomKbps / 1000:F1} Mbps)"
            : $"{Quality} = {SuggestedBitrateKbps} kbps (headroom {HeadroomKbps / 1000:F1} Mbps)";
}

/// <summary>One rung of the quality ladder.</summary>
public sealed class QualityStep
{
    public QualityStep(int bitrateKbps, string label, string description)
    {
        BitrateKbps = bitrateKbps;
        Label = label;
        Description = description;
    }

    public int BitrateKbps { get; }

    /// <summary>Short name for the quality picker, e.g. "720p".</summary>
    public string Label { get; }

    /// <summary>Full description shown under the label, e.g. "720p at 4 Mbps".</summary>
    public string Description { get; }

    public override string ToString() => Description;
}

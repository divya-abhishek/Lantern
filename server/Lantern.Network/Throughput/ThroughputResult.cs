namespace Lantern.Network.Throughput;

/// <summary>
/// What a <see cref="ThroughputProbe"/> measured, plus the playback decision derived from it.
/// </summary>
public sealed class ThroughputResult
{
    /// <summary>A measurement this small is noise (server hiccup, tiny link): do not trust it.</summary>
    public const long MinimumReliableBytes = 256 * 1024;

    /// <summary>Below this window the rate swings with TCP slow start.</summary>
    public static readonly TimeSpan MinimumReliableElapsed = TimeSpan.FromMilliseconds(200);

    /// <summary>Bytes downloaded during the measured phase.</summary>
    public long BytesReceived { get; init; }

    /// <summary>Length of the measured phase (warm-up excluded).</summary>
    public TimeSpan Elapsed { get; init; }

    public int ParallelConnections { get; init; }

    /// <summary>Bytes requested per download.</summary>
    public int ChunkBytes { get; init; }

    public double MegabitsPerSecond { get; init; }

    public double MegabytesPerSecond => MegabitsPerSecond / 8;

    /// <summary>Quality to offer for the next playback, derived from the measurement.</summary>
    public ThroughputRecommendation Recommendation { get; init; } = ThroughputRecommendation.For(0);

    /// <summary>True when enough traffic moved for the number to mean something.</summary>
    public bool IsReliable => BytesReceived >= MinimumReliableBytes && Elapsed >= MinimumReliableElapsed;

    /// <summary>True when the link can carry the original file untouched.</summary>
    public bool DirectPlayPossible => IsReliable && Recommendation.DirectPlayPossible;

    /// <summary>Quality to transcode to (null when direct play is possible).</summary>
    public int? SuggestedBitrateKbps => IsReliable ? Recommendation.SuggestedBitrateKbps : ThroughputRecommendation.Fallback.BitrateKbps;

    public override string ToString() =>
        IsReliable
            ? $"{MegabitsPerSecond:F1} Mbps ({BytesReceived:N0} bytes in {Elapsed.TotalSeconds:F2}s) -> {Recommendation}"
            : $"unreliable measurement ({BytesReceived:N0} bytes in {Elapsed.TotalSeconds:F2}s) -> {Recommendation}";
}

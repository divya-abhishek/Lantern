namespace Lantern.Core.Models;

/// <summary>
/// Persisted playback position for a (media item, client) pair. The server owns
/// the position so resume works across reinstall, app crash or a second device.
/// </summary>
public sealed class PlaybackProgressRecord
{
    public long Id { get; set; }
    public long MediaId { get; set; }

    /// <summary>Client/device id (trusted device id). Empty string means "any client".</summary>
    public required string ClientId { get; set; }

    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public PlaybackState State { get; set; } = PlaybackState.Idle;
    public float PlaybackRate { get; set; } = 1.0f;

    /// <summary>Set when the item is considered finished (>= 95% watched).</summary>
    public bool Completed { get; set; }

    /// <summary>True when the user explicitly restarted from the beginning.</summary>
    public bool? ExplicitReset { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public double Percent => DurationMs > 0
        ? Math.Clamp(PositionMs * 100.0 / DurationMs, 0, 100)
        : 0;
}

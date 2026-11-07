namespace Lantern.Core.Streaming;

/// <summary>
/// Describes a live stream (either a direct file stream or a transcode session)
/// so the diagnostics screen and the PC UI can show what is happening.
/// </summary>
public sealed class StreamTelemetry
{
    public required string StreamId { get; init; }
    public long MediaId { get; init; }
    public required string Title { get; init; }
    public required string DeviceId { get; init; }
    public bool DirectPlay { get; init; }
    public string? ClientIp { get; set; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    private long _bytesSent;
    private long _rangeRequests;
    private double _windowBytes;
    private DateTime _windowStartUtc = DateTime.UtcNow;
    private double _instantMbps;

    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long RangeRequests => Interlocked.Read(ref _rangeRequests);
    public double InstantMbps => _instantMbps;

    public void AddBytes(int count)
    {
        Interlocked.Add(ref _bytesSent, count);
        lock (this)
        {
            _windowBytes += count;
            var elapsed = (DateTime.UtcNow - _windowStartUtc).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _instantMbps = _windowBytes * 8 / elapsed / 1_000_000;
                _windowBytes = 0;
                _windowStartUtc = DateTime.UtcNow;
            }
        }
    }

    public void CountRangeRequest() => Interlocked.Increment(ref _rangeRequests);

    public double AverageMbps
    {
        get
        {
            var elapsed = (LastActivityUtc - StartedUtc).TotalSeconds;
            return elapsed <= 0.5 ? 0 : BytesSent * 8 / elapsed / 1_000_000;
        }
    }
}

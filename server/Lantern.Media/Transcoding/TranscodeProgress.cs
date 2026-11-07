using System.Globalization;
using Lantern.Core.Dtos;

namespace Lantern.Media.Transcoding;

/// <summary>Lifecycle of one HLS transcode session.</summary>
public enum TranscodeState
{
    Starting = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Stopped = 4,
}

/// <summary>
/// Live counters parsed from <c>ffmpeg -progress</c>. Mutated from the stderr reader
/// thread, read from request threads; every field is written under a lock-free
/// volatile style access because values are independently meaningful (a slightly
/// stale snapshot is fine for a progress read-out).
/// </summary>
public sealed class TranscodeProgress
{
    private long _outTimeMs;
    private long _totalSizeBytes;
    private double _speed = 1;
    private double _fps;
    private long _bitrateBps;
    private int _segments;

    /// <summary>Media time ffmpeg has already written (relative to the session start).</summary>
    public long OutTimeMs => Interlocked.Read(ref _outTimeMs);

    public long TotalSizeBytes => Interlocked.Read(ref _totalSizeBytes);
    public double Speed => Volatile.Read(ref _speed);
    public double Fps => Volatile.Read(ref _fps);
    public long BitrateBps => Interlocked.Read(ref _bitrateBps);
    public int SegmentCount => Volatile.Read(ref _segments);
    public string? LastLine { get; private set; }
    public bool Completed { get; private set; }

    public void SetSegments(int count) => Volatile.Write(ref _segments, count);

    /// <summary>
    /// Records the output size measured from the session folder. Needed because the HLS
    /// muxer prints <c>total_size=N/A</c>, which leaves the parsed counters empty. The
    /// average bitrate is derived when ffmpeg reported none of its own.
    /// </summary>
    public void SetMeasuredOutput(long bytes, long outTimeMs)
    {
        Interlocked.Exchange(ref _totalSizeBytes, bytes);

        if (bytes <= 0 || outTimeMs <= 0) return;
        if (Interlocked.Read(ref _bitrateBps) > 0) return;

        Volatile.Write(ref _bitrateBps, (long)(bytes * 8.0 / (outTimeMs / 1000.0)));
    }

    /// <summary>Applies one <c>key=value</c> line from the ffmpeg progress stream.</summary>
    public void Apply(string key, string value)
    {
        switch (key)
        {
            case "out_time_us":
            case "out_time_ms":
                // Both keys carry microseconds; the "_ms" name is a long standing ffmpeg quirk.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                {
                    Interlocked.Exchange(ref _outTimeMs, us / 1000);
                }
                break;
            case "out_time":
                Interlocked.Exchange(ref _outTimeMs, ParseTimecode(value));
                break;
            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                {
                    Interlocked.Exchange(ref _totalSizeBytes, bytes);
                }
                break;
            case "bitrate":
                // The HLS muxer prints "N/A"; never let that wipe a measured value.
                if (TryParseBitrate(value, out var bitrate))
                {
                    Interlocked.Exchange(ref _bitrateBps, bitrate);
                }
                break;
            case "fps":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                {
                    Volatile.Write(ref _fps, fps);
                }
                break;
            case "speed":
                Volatile.Write(ref _speed, ParseSpeed(value));
                break;
            case "progress":
                Completed = value.StartsWith("end", StringComparison.OrdinalIgnoreCase);
                break;
            default:
                LastLine = key;
                return;
        }
        LastLine = $"{key}={value}";
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _outTimeMs, 0);
        Interlocked.Exchange(ref _totalSizeBytes, 0);
        Interlocked.Exchange(ref _bitrateBps, 0);
        Volatile.Write(ref _speed, 1);
        Volatile.Write(ref _fps, 0);
        Volatile.Write(ref _segments, 0);
        Completed = false;
        LastLine = null;
    }

    private static double ParseSpeed(string value)
    {
        var trimmed = value.Trim().TrimEnd('x', 'X');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0
            ? speed
            : 1.0;
    }

    private static bool TryParseBitrate(string value, out long bitrate)
    {
        bitrate = 0;

        // "1234.5kbits/s", "5.2Mbits/s" or "N/A" (the HLS muxer never reports a size).
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('N') || trimmed.StartsWith('n')) return false;

        var suffix = trimmed.Contains("kbit", StringComparison.OrdinalIgnoreCase) ? 1000
            : trimmed.Contains("mbit", StringComparison.OrdinalIgnoreCase) ? 1_000_000
            : 1;
        var digits = new string(trimmed.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) return false;

        bitrate = (long)(rate * suffix);
        return true;
    }

    private static long ParseTimecode(string value)
    {
        // HH:MM:SS.micro
        var parts = value.Trim().Split(':');
        if (parts.Length < 3) return 0;
        if (!int.TryParse(parts[0], out var hours)) return 0;
        if (!int.TryParse(parts[1], out var minutes)) return 0;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return 0;
        return (long)((hours * 3600 + minutes * 60 + seconds) * 1000);
    }
}

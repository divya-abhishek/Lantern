using System.Collections.Concurrent;
using Lantern.Core.Dtos;
using Lantern.Core.Streaming;

namespace Lantern.Server.Services;

/// <summary>
/// Live byte/range counters for the streams this process is serving. Entries are keyed
/// by (media, device) so the Range requests of one playback session add up instead of
/// creating a new row per HTTP request.
/// </summary>
public sealed class StreamRegistry
{
    private readonly ConcurrentDictionary<string, StreamTelemetry> _streams = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _streams.Count;

    /// <summary>Returns (creating it once) the telemetry entry for a playback session.</summary>
    public StreamTelemetry Begin(
        long mediaId, string title, string deviceId, bool directPlay, string? clientIp = null)
    {
        var key = Key(mediaId, deviceId);
        var telemetry = _streams.GetOrAdd(key, _ => new StreamTelemetry
        {
            StreamId = key,
            MediaId = mediaId,
            Title = string.IsNullOrWhiteSpace(title) ? "Unknown" : title,
            DeviceId = deviceId,
            DirectPlay = directPlay,
            ClientIp = clientIp,
        });

        telemetry.LastActivityUtc = DateTime.UtcNow;
        return telemetry;
    }

    public void End(long mediaId, string deviceId) => _streams.TryRemove(Key(mediaId, deviceId), out _);

    /// <summary>Diagnostics snapshot; entries idle for longer than <paramref name="idleTimeout"/> are dropped.</summary>
    public List<StreamStatDto> Snapshot(TimeSpan? idleTimeout = null)
    {
        var cutoff = DateTime.UtcNow - (idleTimeout ?? TimeSpan.FromMinutes(2));
        var stats = new List<StreamStatDto>();

        foreach (var (key, telemetry) in _streams.ToArray())
        {
            if (telemetry.LastActivityUtc < cutoff)
            {
                _streams.TryRemove(key, out _);
                continue;
            }

            stats.Add(new StreamStatDto
            {
                MediaId = telemetry.MediaId,
                Title = telemetry.Title,
                DeviceId = telemetry.DeviceId,
                StartedUtc = telemetry.StartedUtc,
                BytesSent = telemetry.BytesSent,
                DurationMs = (long)Math.Max(0, (telemetry.LastActivityUtc - telemetry.StartedUtc).TotalMilliseconds),
                AverageMbps = telemetry.AverageMbps,
                InstantMbps = telemetry.InstantMbps,
                DirectPlay = telemetry.DirectPlay,
                RangeRequests = telemetry.RangeRequests,
                LastActivityUtc = telemetry.LastActivityUtc,
            });
        }

        return stats.OrderByDescending(s => s.LastActivityUtc).ToList();
    }

    private static string Key(long mediaId, string deviceId) => $"{mediaId}:{deviceId}";
}

/// <summary>
/// Pass-through stream that reports how many bytes the client actually pulled, so the
/// diagnostics screen can show the real stream rate instead of the file bitrate.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<int> _onBytes;
    private readonly Action? _onActivity;

    public CountingStream(Stream inner, Action<int> onBytes, Action? onActivity = null)
    {
        _inner = inner;
        _onBytes = onBytes;
        _onActivity = onActivity;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        CountAsync(_inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        CountAsync(_inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)).AsTask();

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Count(int read)
    {
        if (read > 0) Report(read);
        return read;
    }

    private async ValueTask<int> CountAsync(ValueTask<int> pending)
    {
        var read = await pending.ConfigureAwait(false);
        if (read > 0) Report(read);
        return read;
    }

    private void Report(int bytes)
    {
        _onBytes(bytes);
        _onActivity?.Invoke();
    }
}

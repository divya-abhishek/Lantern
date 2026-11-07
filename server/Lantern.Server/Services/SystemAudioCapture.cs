namespace Lantern.Server.Services;

using System.Threading.Channels;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lantern.Media.Mirroring;
using NAudio.CoreAudioApi;
using NAudio.Wave;
internal readonly record struct PcmBlock(byte[] Data, long StartFrame, long PacketQpc100ns);
public sealed class SystemAudioCapture : IDisposable
{
    private const int SilenceBlockMs = 10;
    private const int MaxContentAgeMs = 250;

    private readonly MMDevice? _device;
    private readonly AudioClient _client;
    private readonly WaveFormat _format;
    private readonly Channel<PcmBlock> _queue;
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _pump;
    private Thread? _reader;
    private CaptureAudioClock? _clock;
    private Stream? _target;
    private long _droppedBytes;
    private long _capturedBytes;
    private long _idleMs;
    private int _contentLagMs;
    private long _captureStartedQpc;
    private int _deliveryGraceFrames;
    private string? _failure;
    private bool _disposed;
    public string? Failure => Volatile.Read(ref _failure);
    private static long Qpc100ns => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    private SystemAudioCapture(MMDevice device, AudioClient client, WaveFormat format)
    {
        _device = device;
        _client = client;
        _format = format;
        SampleRate = format.SampleRate;
        Channels = format.Channels;

        _queue = Channel.CreateBounded<PcmBlock>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
    }

    public int SampleRate { get; }

    public int Channels { get; }
    public long CaptureStartedMicros { get; private set; }

    public string DeviceName => _device?.FriendlyName ?? "Default playback device";
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    public long CapturedBytes => Interlocked.Read(ref _capturedBytes);
    public long SilenceMs => Interlocked.Read(ref _idleMs);
    public int ContentLagMs => Volatile.Read(ref _contentLagMs);
    public static MirrorAudioAvailability Availability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MirrorAudioAvailability(
                false, null, "System audio capture is a Windows feature (WASAPI loopback).");
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            if (device is null)
            {
                return new MirrorAudioAvailability(
                    false, null, "Windows reports no active playback device, so there is no system audio to capture.");
            }

            return new MirrorAudioAvailability(true, device.FriendlyName, null);
        }
        catch (Exception ex)
        {
            return new MirrorAudioAvailability(false, null, $"WASAPI loopback is unavailable: {ex.Message}");
        }
    }
    public static (SystemAudioCapture? Capture, string? Reason) TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return (null, "System audio capture is Windows only.");

        MMDevice? device = null;
        AudioClient? client = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            client = device.AudioClient;
            var format = client.MixFormat.AsStandardWaveFormat();
            if (format.BitsPerSample == 16 && format.Encoding == WaveFormatEncoding.Pcm)
            {
                return (new SystemAudioCapture(device, client, format), null);
            }

            if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return (new SystemAudioCapture(device, client, format), null);
            }

            client.Dispose();
            device.Dispose();
            return (null,
                $"The playback device uses {format.BitsPerSample}-bit {format.Encoding}; " +
                "only 16-bit PCM and 32-bit float are supported.");
        }
        catch (Exception ex)
        {
            client?.Dispose();
            device?.Dispose();
            return (null, $"WASAPI loopback could not be opened: {ex.Message}");
        }
    }
    public void BeginCapture()
    {
        _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
            400_000, 0, _client.MixFormat, Guid.Empty);
        _deliveryGraceFrames = _client.BufferSize + SampleRate / 50;
        _captureStartedQpc = Qpc100ns;
        CaptureStartedMicros = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        _clock = new CaptureAudioClock(SampleRate, _captureStartedQpc);
        _client.Start();
        _reader = new Thread(ReadPackets) { IsBackground = true, Name = "Lantern WASAPI packets" };
        _reader.Start();
    }
    public void AttachTo(Stream target)
    {
        _target = target;
        _pump ??= Task.Run(() => PumpAsync(_lifetime.Token));
    }

    private void ReadPackets()
    {
        try
        {
            var packets = _client.AudioCaptureClient;
            while (!_lifetime.IsCancellationRequested)
            {
                while (packets.GetNextPacketSize() > 0 && !_lifetime.IsCancellationRequested)
                {
                    var pointer = packets.GetBuffer(out var frames, out var flags, out var position, out var qpc);
                    byte[] pcm;
                    long start;
                    try
                    {
                        var raw = new byte[frames * _format.BlockAlign];
                        if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(pointer, raw, 0, raw.Length);
                        pcm = ToPcm16(raw, raw.Length);
                        // QPC stamps the first sample, not when the managed callback happened.
                        // Ignore a bad packet timestamp unless establishing the initial anchor.
                        var badTimestamp = (flags & AudioClientBufferFlags.TimestampError) != 0;
                        if (badTimestamp) qpc = Qpc100ns - frames * 10_000_000L / SampleRate;
                        start = _clock!.Position(position, qpc,
                            !badTimestamp && (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                    }
                    finally { packets.ReleaseBuffer(frames); }
                    Interlocked.Add(ref _capturedBytes, pcm.Length);
                    var block = new PcmBlock(pcm, start, qpc);
                    if (!_queue.Writer.TryWrite(block))
                    {
                        if (_queue.Reader.TryRead(out var stale)) Interlocked.Add(ref _droppedBytes, stale.Data.Length);
                        if (!_queue.Writer.TryWrite(block)) Interlocked.Add(ref _droppedBytes, pcm.Length);
                    }
                }
                _lifetime.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception ex) { Volatile.Write(ref _failure, ex.Message); _lifetime.Cancel(); }
        finally { _queue.Writer.TryComplete(); }
    }
    // Device positions preserve every sample in contiguous packets despite reader scheduling jitter.
    private async Task PumpAsync(CancellationToken ct)
    {
        var frameBytes = Channels * 2;
        var silence = new byte[SampleRate / 100 * frameBytes];
        long writtenFrames = 0;
        long FramesAt(long qpc) => (qpc - _captureStartedQpc) * SampleRate / 10_000_000;
        async Task FillTo(long end)
        {
            while (writtenFrames < end)
            {
                var frames = (int)Math.Min(end - writtenFrames, silence.Length / frameBytes);
                await _target!.WriteAsync(silence.AsMemory(0, frames * frameBytes), ct).ConfigureAwait(false);
                writtenFrames += frames;
                Interlocked.Add(ref _idleMs, frames * 1000 / SampleRate);
            }
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = Qpc100ns;
                if (_queue.Reader.TryRead(out var block))
                {
                    var frames = block.Data.Length / frameBytes;
                    var start = block.StartFrame;
                    var end = start + frames;
                    var age = Math.Max(0, (now - block.PacketQpc100ns) / 10_000 - frames * 1000 / SampleRate);
                    Volatile.Write(ref _contentLagMs, (int)age);
                    if (age > MaxContentAgeMs || end <= writtenFrames)
                    {
                        Interlocked.Add(ref _droppedBytes, block.Data.Length);
                        continue;
                    }
                    await FillTo(start).ConfigureAwait(false);
                    var skip = (int)Math.Clamp(writtenFrames - start, 0, frames);
                    Interlocked.Add(ref _droppedBytes, skip * frameBytes);
                    await _target!.WriteAsync(block.Data.AsMemory(skip * frameBytes), ct).ConfigureAwait(false);
                    writtenFrames += frames - skip;
                }
                else
                {
                    // Only advance silence behind the actual allocated device buffer, allowing
                    // packets still held by WASAPI to arrive without destroying their samples.
                    await FillTo(Math.Max(0, FramesAt(now) - _deliveryGraceFrames)).ConfigureAwait(false);
                    await Task.Delay(SilenceBlockMs, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private byte[] ToPcm16(byte[] buffer, int count)
    {
        var format = _format;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = count / 4;
            var output = new byte[samples * 2];

            for (var i = 0; i < samples; i++)
            {
                var value = Math.Clamp(BitConverter.ToSingle(buffer, i * 4), -1f, 1f);
                var scaled = (short)Math.Round(value * short.MaxValue);
                output[i * 2] = (byte)scaled;
                output[i * 2 + 1] = (byte)(scaled >> 8);
            }

            return output;
        }
        var length = count - (count % 2);
        var copy = new byte[length];
        Buffer.BlockCopy(buffer, 0, copy, 0, length);
        return copy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _reader?.Join(); // Reader releases native packets before the client is disposed.
        try { _client.Stop(); } catch { }
        _queue.Writer.TryComplete();

        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (Exception)
        {
        }

        _client.Dispose();
        _device?.Dispose();
        _lifetime.Dispose();
    }
}


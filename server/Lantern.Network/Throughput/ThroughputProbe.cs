using System.Net.Http;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lantern.Network.Throughput;

/// <summary>
/// Measures the real throughput between this machine and a Lantern server by downloading
/// random bytes over HTTP, exactly like the client does before it picks a quality.
/// </summary>
/// <remarks>
/// <para>
/// The probe deliberately reuses the HTTP path (and therefore the same TCP/Wi-Fi conditions)
/// the movie stream will use: the endpoint is the server's throughput route, which answers with
/// incompressible payload so the number cannot be inflated by proxies.
/// </para>
/// <para>
/// Several connections run at once (a single TCP stream usually under-reports Wi-Fi), a short
/// warm-up phase is discarded, and the rate is computed over the measured phase only.
/// </para>
/// </remarks>
public sealed class ThroughputProbe : IDisposable
{
    private readonly HttpClient? _ownedClient;
    private readonly ILogger _log;
    private bool _disposed;

    public ThroughputProbe(HttpClient? client = null, ILogger<ThroughputProbe>? logger = null)
    {
        _log = (ILogger?)logger ?? NullLogger.Instance;

        if (client is not null)
        {
            _ownedClient = null;
            Client = client;
            return;
        }

        _ownedClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            // The measurement window – not the HTTP timeout – decides when to stop.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        Client = _ownedClient;
    }

    public HttpClient Client { get; }

    /// <summary>Parallel downloads; three covers Wi-Fi aggregation without flooding a phone.</summary>
    public int ParallelConnections { get; init; } = 3;

    /// <summary>Bytes requested per download before the connection is recycled.</summary>
    public int ChunkBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>TCP slow start / first-connection cost that is thrown away.</summary>
    public TimeSpan Warmup { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Length of the measured phase.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Query parameter the server reads to decide how much data to send.</summary>
    public string ByteCountParameter { get; init; } = "bytes";

    /// <summary>Runs one measurement; failures are reported through <see cref="ThroughputResult.IsReliable"/>.</summary>
    public async Task<ThroughputResult> RunAsync(Uri endpoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (Duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(endpoint), "Duration must be positive.");

        var connections = Math.Clamp(ParallelConnections, 1, 16);
        var skipWarmup = Warmup <= TimeSpan.Zero || Warmup >= Duration;
        if (!skipWarmup) await PumpAsync(endpoint, Warmup, connections, counter: null, ct).ConfigureAwait(false);

        var counter = new ByteCounter();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await PumpAsync(endpoint, Duration, connections, counter, ct).ConfigureAwait(false);
        stopwatch.Stop();

        var bytes = counter.Value;
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var megabitsPerSecond = seconds > 0 ? bytes * 8.0 / seconds / 1_000_000 : 0;

        var result = new ThroughputResult
        {
            BytesReceived = bytes,
            Elapsed = stopwatch.Elapsed,
            ParallelConnections = connections,
            ChunkBytes = ChunkBytes,
            MegabitsPerSecond = megabitsPerSecond,
            Recommendation = ThroughputRecommendation.For(megabitsPerSecond),
        };

        if (result.IsReliable)
        {
            _log.LogInformation(
                "Throughput probe on {Endpoint}: {Rate:F1} Mbps over {Connections} connections -> {Recommendation}",
                endpoint, result.MegabitsPerSecond, connections, result.Recommendation);
        }
        else
        {
            _log.LogWarning(
                "Throughput probe on {Endpoint} measured only {Bytes:N0} bytes in {Seconds:F2}s; falling back to {Fallback}",
                endpoint, bytes, seconds, ThroughputRecommendation.Fallback);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownedClient?.Dispose();
    }

    /// <summary>Runs <paramref name="connections"/> downloads until <paramref name="runFor"/> elapsed.</summary>
    private Task PumpAsync(Uri endpoint, TimeSpan runFor, int connections, ByteCounter? counter, CancellationToken ct)
    {
        var workers = new Task[connections];
        for (var i = 0; i < connections; i++) workers[i] = WorkerAsync(endpoint, runFor, i, counter, ct);
        return Task.WhenAll(workers);
    }

    private async Task WorkerAsync(Uri endpoint, TimeSpan runFor, int worker, ByteCounter? counter, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);

        try
        {
            var request = 0;
            while (!ct.IsCancellationRequested && stopwatch.Elapsed < runFor)
            {
                using var response = await Client
                    .GetAsync(BuildRequestUri(endpoint, worker, request++), HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogDebug("Throughput probe: {Endpoint} answered {Status}", endpoint, (int)response.StatusCode);
                    return;
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int read;
                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    counter?.Add(read);
                    if (ct.IsCancellationRequested || stopwatch.Elapsed >= runFor) return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex) { _log.LogDebug(ex, "Throughput probe: request to {Endpoint} failed", endpoint); }
        catch (IOException ex) { _log.LogDebug(ex, "Throughput probe: stream to {Endpoint} broke", endpoint); }
        catch (ObjectDisposedException) { }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Adds the byte count and a cache buster so no layer can serve a cached response.</summary>
    private Uri BuildRequestUri(Uri endpoint, int worker, int request)
    {
        var builder = new UriBuilder(endpoint);
        var query = string.IsNullOrEmpty(builder.Query) ? "?" : builder.Query + "&";
        builder.Query = $"{query}{ByteCountParameter}={ChunkBytes}&worker={worker}&n={request}";
        return builder.Uri;
    }

    /// <summary>Thread safe byte total shared by the workers.</summary>
    private sealed class ByteCounter
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Add(int count) => Interlocked.Add(ref _value, count);
    }
}


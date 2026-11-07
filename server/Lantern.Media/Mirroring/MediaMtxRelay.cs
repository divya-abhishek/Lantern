namespace Lantern.Media.Mirroring;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lantern.Media.Processes;
using Microsoft.Extensions.Logging;

/// <summary>Everything the relay needs to come up for one mirroring session.</summary>
public sealed record MediaMtxRelayOptions
{
    /// <summary>Full path to the relay executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Address the relay listens on. Empty or 0.0.0.0 means every interface.</summary>
    public string BindAddress { get; init; } = string.Empty;

    /// <summary>Address a receiver on the LAN should use. Empty falls back to the bind address.</summary>
    public string AdvertisedAddress { get; init; } = string.Empty;

    /// <summary>Folder part of the session path, e.g. "lantern".</summary>
    public string PathPrefix { get; init; } = "lantern";

    /// <summary>RTSP port. Zero picks the first free candidate.</summary>
    public int Port { get; init; }

    /// <summary>The relay's own HTTP API port, bound to loopback only. Zero picks a free one.</summary>
    public int ApiPort { get; init; }

    public string LogLevel { get; init; } = "info";
}

/// <summary>
/// What the relay reports about one session's path: the bytes it took from the publisher, the bytes
/// it sent to receivers, how many receivers are attached and whether any of them had packets thrown
/// away because it could not keep up. All of it is read from the relay rather than modelled.
/// </summary>
public sealed record MediaMtxRelayStats(
    bool Online,
    long BytesReceived,
    long BytesSent,
    int Readers,
    long ReaderDiscardedPackets,
    IReadOnlyList<string> Tracks)
{
    public bool HasVideo => Tracks.Any(t => t.Contains("H264", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One running MediaMTX relay, owned by one mirroring session.
///
/// This replaces the custom chunked MPEG-TS transport: the server no longer muxes, frames, fans out
/// or paces anything for the receivers. ffmpeg publishes H.264 + AAC to the relay over RTSP/TCP, and
/// the relay serves every receiver from the live edge. The reasons are written down in
/// docs/PHASE-3-STATUS.md; the short version is that a purpose-built relay does this job correctly
/// and nothing in the hand-written one was load-bearing for the rest of Lantern.
///
/// Two rules shape the configuration this writes, and both are about not publishing anything by
/// accident:
///  - the relay listens on this machine's LAN address and nowhere else, and it is stopped with the
///    session it belongs to;
///  - publishing and reading both require credentials, generated per session. The projector only
///    ever receives the read URL through Lantern's authenticated API, and the path itself carries a
///    128-bit random token, so there is no permanent anonymous stream URL to find or to reuse.
/// </summary>
public sealed class MediaMtxRelay : IAsyncDisposable
{
    /// <summary>Port candidates: the documented default, then a few neighbours, then anything free.</summary>
    private static readonly int[] PortCandidates = [8554, 8555, 8556, 8557, 8558];

    private static readonly int[] ApiPortCandidates = [9997, 9998, 9999, 10000];

    /// <summary>How long the relay gets to open its listener before the session gives up on it.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Readiness is a TCP connect, so this is only the gap between attempts.</summary>
    private const int StartupPollMs = 150;

    /// <summary>Kept for the failure message and the log: the last lines the relay said.</summary>
    private const int LogRingSize = 40;

    private static readonly HttpClient Api = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly string _workDirectory;
    private readonly Queue<string> _recentLog = new();

    private int _disposed;

    private MediaMtxRelay(
        Process process,
        MediaMtxRelayOptions options,
        string workDirectory,
        string sessionPath,
        string user,
        string password,
        int port,
        int apiPort,
        ILogger logger)
    {
        _process = process;
        _logger = logger;
        _workDirectory = workDirectory;

        ExecutablePath = options.ExecutablePath;
        SessionPath = sessionPath;
        User = user;
        Password = password;
        Port = port;
        ApiPort = apiPort;
        BindAddress = options.BindAddress;
        AdvertisedAddress = string.IsNullOrWhiteSpace(options.AdvertisedAddress)
            ? options.BindAddress
            : options.AdvertisedAddress;
        StartedUtc = DateTime.UtcNow;
    }

    /// <summary>The relay executable this session is using.</summary>
    public string ExecutablePath { get; }

    /// <summary>The session's own path on the relay, e.g. <c>lantern/9f0c…</c>.</summary>
    public string SessionPath { get; }

    public string User { get; }

    public string Password { get; }

    /// <summary>RTSP port the relay is listening on for this session.</summary>
    public int Port { get; }

    /// <summary>Loopback-only HTTP API port used for the live stats.</summary>
    public int ApiPort { get; }

    /// <summary>The interface the relay listens on (empty = every interface).</summary>
    public string BindAddress { get; }

    /// <summary>The address a LAN receiver connects to.</summary>
    public string AdvertisedAddress { get; }

    public DateTime StartedUtc { get; }

    public int ProcessId => _process.Id;

    public bool IsRunning => !_process.HasExited;

    /// <summary>
    /// The URL ffmpeg publishes to. It carries the publish credential so that nothing else on the LAN
    /// can push a picture into this session's path; the URL never leaves this machine.
    /// </summary>
    public string PublishUrl => BuildUrl(HostForPublish());

    /// <summary>
    /// The URL the projector reads, for a caller on the LAN. Handed out only by the authenticated mirror
    /// API, and it dies with the session: the path token, the credential and the port are all new every
    /// time. <paramref name="host"/> is the address the caller should dial, so a machine with several
    /// interfaces answers each device with the address that device can actually reach.
    /// </summary>
    public string ReadUrlFor(string? host) =>
        BuildUrl(string.IsNullOrWhiteSpace(host) ? HostForRead() : host);

    /// <summary>The read URL for the server's own primary LAN address.</summary>
    public string ReadUrl => ReadUrlFor(null);

    /// <summary>The last lines the relay said, for a failure message that explains something.</summary>
    public string LogTail
    {
        get
        {
            lock (_recentLog) return string.Join("\n", _recentLog);
        }
    }

    /// <summary>
    /// Writes the per-session configuration, starts the relay and waits until its listener is
    /// actually accepting connections. Throws with the relay's own words when it will not come up: a
    /// port clash and a broken executable are different problems, and its log says which one it is.
    /// </summary>
    public static async Task<MediaMtxRelay> StartAsync(
        MediaMtxRelayOptions options,
        ILogger logger,
        CancellationToken ct = default)
    {
        var token = RandomToken(16);
        var sessionPath = $"{options.PathPrefix}/{token}";
        var user = "lantern";
        var password = RandomToken(16);

        var workDirectory = Path.Combine(
            Path.GetTempPath(), "Lantern", "mirror", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{token}");
        Directory.CreateDirectory(workDirectory);

        var port = options.Port > 0 ? options.Port : FreePort(options.BindAddress, PortCandidates);
        var apiPort = options.ApiPort > 0 ? options.ApiPort : FreePort("127.0.0.1", ApiPortCandidates);

        var configPath = Path.Combine(workDirectory, "mediamtx.yml");
        await File.WriteAllTextAsync(
                configPath,
                BuildConfiguration(options, sessionPath, user, password, port, apiPort),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ct)
            .ConfigureAwait(false);

        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(configPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var relay = new MediaMtxRelay(
            process, options, workDirectory, sessionPath, user, password, port, apiPort, logger);

        process.OutputDataReceived += (_, e) => relay.Remember(e.Data);
        process.ErrorDataReceived += (_, e) => relay.Remember(e.Data);

        try
        {
            process.Start();
        Lantern.Core.ProcessOwnership.Attach(process);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The screen mirror relay could not be started ({options.ExecutablePath}): {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (await relay.WaitForListenerAsync(ct).ConfigureAwait(false)) return relay;

        var reason = process.HasExited
            ? $"The screen mirror relay exited with code {process.ExitCode}."
            : $"The screen mirror relay did not open port {port} in time.";

        var tail = relay.LogTail;
        await relay.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException($"{reason} {tail}".Trim());
    }

    /// <summary>The relay's configuration for one session. Written from scratch every time.</summary>
    private static string BuildConfiguration(
        MediaMtxRelayOptions options,
        string sessionPath,
        string user,
        string password,
        int port,
        int apiPort)
    {
        // Only the RTSP server is wanted. RTMP, HLS, WebRTC, SRT and MoQ would each open another
        // listener and another way to reach the stream, so every one of them is turned off: the mirror
        // is one transport, and the projector is the only thing meant to be able to open it.
        var listen = string.IsNullOrWhiteSpace(options.BindAddress)
                     || options.BindAddress is "0.0.0.0" or "::"
            ? $":{port}"
            : $"{options.BindAddress}:{port}";

        return $$"""
                # Written by Lantern for one screen mirroring session. Regenerated on every start;
                # edits are lost and nothing in here is user-facing.
                logLevel: {{options.LogLevel}}
                logDestinations: [stdout]
                readTimeout: 10s
                writeTimeout: 10s

                rtsp: yes
                rtspAddress: {{listen}}
                # TCP only: a reliable LAN beats theoretical UDP latency, and interleaving RTP in the
                # RTSP connection means one port to reach instead of a dynamic range of them.
                rtspTransports: [tcp]
                rtspEncryption: "no"
                rtspAuthMethods: [basic]

                rtmp: no
                hls: no
                webrtc: no
                srt: no
                moq: no
                playback: false

                # Loopback only: these numbers are read by this server, not by the projector.
                api: yes
                apiAddress: 127.0.0.1:{{apiPort}}
                metrics: no
                pprof: no

                authMethod: internal
                authInternalUsers:
                  - user: {{user}}
                    pass: {{password}}
                    permissions:
                      - action: publish
                        path: {{sessionPath}}
                      - action: read
                        path: {{sessionPath}}
                  - user: any
                    pass:
                    ips: ["127.0.0.1", "::1"]
                    permissions:
                      - action: api

                paths:
                  {{sessionPath}}: {}
                """;
    }

    /// <summary>
    /// Waits for the RTSP listener to accept a connection. Connecting is the real test: it proves the
    /// socket is open, where a log line only claims it.
    /// </summary>
    private async Task<bool> WaitForListenerAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_process.HasExited) return false;
            if (CanConnect(HostForPublish(), Port)) return true;

            try
            {
                await Task.Delay(StartupPollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks the relay what is happening on this session's path: bytes in, bytes out, receivers.
    /// Returns null when the relay has gone away, which the caller reports as a dead session rather
    /// than letting a status poll throw.
    /// </summary>
    public async Task<MediaMtxRelayStats?> StatsAsync(CancellationToken ct = default)
    {
        // The path is used the way the API expects it - slashes and all - and it is generated here,
        // so there is nothing to escape.
        var url = $"http://127.0.0.1:{ApiPort}/v3/paths/get/{SessionPath}";

        try
        {
            await using var stream = await Api.GetStreamAsync(url, ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);

            var root = document.RootElement;
            var readers = root.TryGetProperty("readers", out var readerArray)
                          && readerArray.ValueKind == JsonValueKind.Array
                ? readerArray
                : default;

            var readerCount = readers.ValueKind == JsonValueKind.Array ? readers.GetArrayLength() : 0;
            var discarded = 0L;

            if (readerCount > 0)
            {
                foreach (var reader in readers.EnumerateArray())
                {
                    discarded += Number(reader, "rtpPacketsDiscarded")
                                 + Number(reader, "outboundRTPPacketsDiscarded");
                }
            }

            var tracks = new List<string>();
            if (root.TryGetProperty("tracks", out var trackArray) && trackArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var track in trackArray.EnumerateArray())
                {
                    if (track.ValueKind == JsonValueKind.String) tracks.Add(track.GetString() ?? string.Empty);
                }
            }

            return new MediaMtxRelayStats(
                Online: root.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.True,
                BytesReceived: Number(root, "bytesReceived"),
                BytesSent: Number(root, "bytesSent"),
                Readers: readerCount,
                ReaderDiscardedPackets: discarded,
                Tracks: tracks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The mirror relay did not answer on its API port {Port}", ApiPort);
            return null;
        }
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        line = line.Replace(Password, "[redacted]", StringComparison.Ordinal);

        lock (_recentLog)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > LogRingSize) _recentLog.Dequeue();
        }

        // ERR/WRN lines are the ones worth surfacing; the rest is routine and goes in at debug level so
        // that a healthy session does not fill the console with relay chatter.
        if (line.Contains(" ERR", StringComparison.Ordinal) || line.Contains(" WRN", StringComparison.Ordinal))
        {
            _logger.LogWarning("mirror relay: {Line}", line);
        }
        else
        {
            _logger.LogDebug("mirror relay: {Line}", line);
        }
    }

    /// <summary>The address ffmpeg publishes to: the listening interface, or loopback when the relay is on every one.</summary>
    private string HostForPublish() =>
        string.IsNullOrWhiteSpace(BindAddress) || BindAddress is "0.0.0.0" or "::"
            ? "127.0.0.1"
            : BindAddress;

    /// <summary>The address a receiver uses: the LAN address when this server has one.</summary>
    private string HostForRead() =>
        string.IsNullOrWhiteSpace(AdvertisedAddress) ? HostForPublish() : AdvertisedAddress;

    private string BuildUrl(string host)
    {
        var credentials = string.IsNullOrEmpty(User)
            ? string.Empty
            : $"{Uri.EscapeDataString(User)}:{Uri.EscapeDataString(Password)}@";

        return $"rtsp://{credentials}{host}:{Port}/{SessionPath}";
    }

    /// <summary>128-bit secret as lowercase hex: URL-safe, nothing to escape, no dictionary words.</summary>
    private static string RandomToken(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    /// <summary>
    /// First port that can be bound on the address the relay will use. Bound and released rather than
    /// assumed: the relay is about to bind that same address, so a port free on loopback proves nothing.
    /// </summary>
    private static int FreePort(string address, int[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (CanBind(address, candidate)) return candidate;
        }

        // Everything on the list is taken: ask the operating system for a port and let the relay use it.
        try
        {
            var probe = new TcpListener(IPAddress.Parse(Resolve(address)), 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
        catch (Exception)
        {
            return candidates[0];
        }
    }

    private static bool CanBind(string address, int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Parse(Resolve(address)), port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanConnect(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromMilliseconds(250)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Resolve(string address) =>
        string.IsNullOrWhiteSpace(address) || address == "0.0.0.0" ? "127.0.0.1" : address;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        ProcessRunner.TryKill(_process, _logger);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A relay that will not die on request has already been killed; the handle is released below.
        }

        _process.Dispose();

        // The configuration holds this session's credential, so it goes with the session.
        try
        {
            await Task.Delay(150).ConfigureAwait(false);
            if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The mirror relay's working folder could not be removed: {Folder}", _workDirectory);
        }
    }





}

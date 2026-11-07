using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lantern.Core.Api;
using Lantern.Core.Dtos;
using Lantern.Core.Messaging;
using Lantern.Core.Models;
using Lantern.Database.Repositories;
using Lantern.Server.Security;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// The WebSocket hub: one socket per client, carrying scan/transcode progress, resume
/// heartbeats, remote control relay and PC-audio sync. Messages are the polymorphic
/// <see cref="LanternMessage"/> types from Core, so the Kotlin client needs one dispatcher.
/// </summary>
public sealed class ConnectionHub
{
    private readonly ServerRuntime _runtime;
    private readonly SettingsStore _settings;
    private readonly MediaRepository _media;
    private readonly ProgressService _progress;
    private readonly ILogger<ConnectionHub> _logger;
    private readonly ConcurrentDictionary<string, ClientChannel> _clients = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionHub(
        ServerRuntime runtime,
        SettingsStore settings,
        MediaRepository media,
        ProgressService progress,
        ILogger<ConnectionHub> logger)
    {
        _runtime = runtime;
        _settings = settings;
        _media = media;
        _progress = progress;
        _logger = logger;
    }

    public int Count => _clients.Count;

    public IReadOnlyList<string> DeviceIds => _clients.Keys.ToArray();

    public bool IsOnline(string deviceId) => _clients.ContainsKey(deviceId);

    public List<ClientConnectionDto> Snapshot() =>
        _clients.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.ToDto()).ToList();

    /// <summary>Receives one client until it disconnects.</summary>
    public async Task HandleAsync(HttpContext http, AuthContext auth, WebSocket socket, CancellationToken ct)
    {
        // A reconnecting client replaces its previous socket rather than piling up.
        if (_clients.TryRemove(auth.DeviceId, out var previous) && previous.Socket is { State: WebSocketState.Open } stale)
        {
            try
            {
                await stale.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced by a newer connection", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing the previous socket of {Device} failed", auth.DeviceName);
            }
        }

        var channel = new ClientChannel(auth, http.Connection.RemoteIpAddress?.ToString()) { Socket = socket };
        _clients[auth.DeviceId] = channel;
        _logger.LogInformation("WebSocket connected: {Device} ({Ip})", channel.Name, channel.IpAddress);

        try
        {
            await SendAsync(channel, BuildHello(await SafeCountsAsync(ct).ConfigureAwait(false)), ct)
                .ConfigureAwait(false);
            await ReceiveLoopAsync(channel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client vanished or the server is stopping.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket failed for {Device}", channel.Name);
        }
        finally
        {
            channel.WebSocketConnected = false;
            if (_clients.TryGetValue(channel.DeviceId, out var current) && ReferenceEquals(current, channel))
            {
                _clients.TryRemove(channel.DeviceId, out _);
            }

            _logger.LogInformation("WebSocket disconnected: {Device}", channel.Name);
        }
    }

    private async Task ReceiveLoopAsync(ClientChannel channel, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();

        while (!ct.IsCancellationRequested && channel.Socket is { State: WebSocketState.Open } socket)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                return;
            }

            payload.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(payload.ToArray());
            payload.SetLength(0);
            channel.LastSeenUtc = DateTime.UtcNow;

            await DispatchAsync(channel, text, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(ClientChannel channel, string text, CancellationToken ct)
    {
        LanternMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<LanternMessage>(text, ApiJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ignoring malformed WebSocket message from {Device}", channel.Name);
            return;
        }

        switch (message)
        {
            case ClientHelloMessage hello:
                channel.Apply(hello);
                await SendAsync(channel, BuildHello(await SafeCountsAsync(ct).ConfigureAwait(false)), ct)
                    .ConfigureAwait(false);
                break;

            case PlaybackReportMessage report:
                await HandlePlaybackReportAsync(channel, report, ct).ConfigureAwait(false);
                break;

            case PlaybackCommandMessage command:
                // Remote control: relay to everyone except the sender (the phone that drives
                // the projector must not receive its own command back).
                await BroadcastAsync(command, exceptDeviceId: channel.DeviceId).ConfigureAwait(false);
                break;

            case PingMessage ping:
                await SendAsync(
                    channel,
                    new PongMessage { Ticks = DateTime.UtcNow.Ticks, ClientTicks = ping.Ticks },
                    ct).ConfigureAwait(false);
                break;

            default:
                _logger.LogDebug(
                    "Unhandled WebSocket message {Type} from {Device}",
                    message?.GetType().Name ?? "null", channel.Name);
                break;
        }
    }

    private async Task HandlePlaybackReportAsync(
        ClientChannel channel, PlaybackReportMessage report, CancellationToken ct)
    {
        var ack = await _progress.ReportAsync(
            new ProgressReportDto
            {
                MediaId = report.MediaId,
                ClientId = channel.DeviceId,
                PositionMs = report.PositionMs,
                DurationMs = report.DurationMs,
                State = report.State,
                PlaybackRate = report.PlaybackRate,
                BufferedMs = report.BufferedMs,
                ObservedBitrateBps = report.ObservedBitrateBps,
                AudioMode = report.AudioMode,
                DroppedFrames = report.DroppedFrames,
                DecoderName = report.DecoderName,
                Reset = report.Reset,
            },
            channel.DeviceId,
            ct).ConfigureAwait(false);

        if (ack is null) return;

        channel.LastMediaId = report.MediaId;

        // The PC UI mirrors what the projector is doing, so the sender is included.
        await BroadcastAsync(new PlaybackStateMessage
        {
            DeviceId = channel.DeviceId,
            MediaId = report.MediaId,
            PositionMs = ack.PositionMs,
            DurationMs = report.DurationMs,
            State = report.State,
            PlaybackRate = report.PlaybackRate,
            BufferedMs = report.BufferedMs,
            Mode = report.Mode,
            AudioMode = ack.AudioMode,
        }).ConfigureAwait(false);
    }

    // --- sending ------------------------------------------------------------

    public Task BroadcastAsync(LanternMessage message, string? exceptDeviceId = null) =>
        SendToAsync(_clients.Values.ToArray(), message, exceptDeviceId);

    public Task SendToDeviceAsync(string deviceId, LanternMessage message) =>
        _clients.TryGetValue(deviceId, out var channel)
            ? SendAsync(channel, message)
            : Task.CompletedTask;

    public Task BroadcastScanStatusAsync(ScanStatusDto status) =>
        BroadcastAsync(new ScanStatusMessage { Status = status });

    public Task BroadcastTranscodeAsync(TranscodeSessionDto session) =>
        BroadcastAsync(new TranscodeStatusMessage { Session = session });

    public Task BroadcastLibraryChangedAsync(
        LibraryCountsDto counts, int added = 0, int removed = 0, IReadOnlyList<long>? changedMediaIds = null) =>
        BroadcastAsync(new LibraryChangedMessage
        {
            Counts = counts,
            Added = added,
            Removed = removed,
            ChangedMediaIds = changedMediaIds is null ? null : [.. changedMediaIds],
        });

    public Task BroadcastErrorAsync(string code, string message, string? detail = null, bool fatal = false) =>
        BroadcastAsync(new ErrorMessage { Code = code, Message = message, Detail = detail, Fatal = fatal });

    /// <summary>Kicks a device off (revoked token) so it stops streaming immediately.</summary>
    public async Task CloseAsync(string deviceId, string reason)
    {
        if (!_clients.TryGetValue(deviceId, out var channel)) return;

        await SendAsync(channel, new ErrorMessage { Code = "revoked", Message = reason, Fatal = true })
            .ConfigureAwait(false);

        if (channel.Socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing the socket of {Device} failed", deviceId);
            }
        }

        _clients.TryRemove(deviceId, out _);
    }

    private async Task SendToAsync(
        IReadOnlyCollection<ClientChannel> channels, LanternMessage message, string? exceptDeviceId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, ApiJson.Options);
        foreach (var channel in channels)
        {
            if (exceptDeviceId is not null
                && string.Equals(channel.DeviceId, exceptDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await SendRawAsync(channel, payload).ConfigureAwait(false);
        }
    }

    private Task SendAsync(ClientChannel channel, LanternMessage message, CancellationToken ct = default) =>
        SendRawAsync(channel, JsonSerializer.SerializeToUtf8Bytes(message, ApiJson.Options));

    private async Task SendRawAsync(ClientChannel channel, byte[] payload)
    {
        if (channel.Socket is not { State: WebSocketState.Open } socket) return;

        await channel.SendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Sending to {Device} failed", channel.Name);
        }
        finally
        {
            channel.SendGate.Release();
        }
    }

    private ServerHelloMessage BuildHello(LibraryCountsDto counts) => new()
    {
        ServerId = _runtime.Identity.ServerId,
        ServerName = _settings.Current.ServerName,
        Version = _runtime.Version,
        Settings = DtoMapper.ClientSettings(_settings.Current, _runtime.Tools),
        Library = counts,
    };

    private async Task<LibraryCountsDto> SafeCountsAsync(CancellationToken ct)
    {
        try
        {
            return await _media.GetCountsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading library counts for the WebSocket hello failed");
            return new LibraryCountsDto();
        }
    }

    /// <summary>One connected client (id, identity and live socket).</summary>
    private sealed class ClientChannel
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ClientChannel(AuthContext auth, string? ipAddress)
        {
            DeviceId = auth.DeviceId;
            Name = auth.DeviceName;
            IpAddress = ipAddress;
            ConnectedUtc = DateTime.UtcNow;
            LastSeenUtc = ConnectedUtc;
        }

        public string DeviceId { get; }
        public string Name { get; private set; }
        public string? Platform { get; private set; }
        public string? AppVersion { get; private set; }
        public string? IpAddress { get; }
        public DateTime ConnectedUtc { get; }
        public DateTime LastSeenUtc { get; set; }
        public long? LastMediaId { get; set; }
        public bool WebSocketConnected { get; set; } = true;
        public WebSocket? Socket { get; set; }
        public SemaphoreSlim SendGate => _gate;

        public void Apply(ClientHelloMessage hello)
        {
            if (!string.IsNullOrWhiteSpace(hello.DeviceName)) Name = hello.DeviceName!;
            Platform = hello.Capabilities?.Platform ?? Platform;
            AppVersion = hello.AppVersion ?? AppVersion;
            LastMediaId = hello.CurrentMediaId ?? LastMediaId;
            LastSeenUtc = DateTime.UtcNow;
        }

        public ClientConnectionDto ToDto() => new()
        {
            DeviceId = DeviceId,
            Name = Name,
            IpAddress = IpAddress,
            ConnectedUtc = ConnectedUtc,
            LastSeenUtc = LastSeenUtc,
            WebSocketConnected = WebSocketConnected,
            Platform = Platform,
        };
    }
}

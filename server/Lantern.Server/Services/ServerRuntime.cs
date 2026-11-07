using System.Net;
using System.Reflection;
using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Lantern.Media.Tooling;
using Lantern.Network;

namespace Lantern.Server.Services;

/// <summary>
/// Process level state shared by every endpoint: identity, settings, uptime, the LAN
/// addresses to advertise and the last known ffmpeg capabilities.
/// </summary>
public sealed class ServerRuntime
{
    public ServerRuntime(ServerIdentity identity, SettingsStore settings)
    {
        Identity = identity;
        Settings = settings;
        StartedUtc = DateTime.UtcNow;
        Version = ResolveVersion();
    }

    public ServerIdentity Identity { get; }

    public SettingsStore Settings { get; }

    public DateTime StartedUtc { get; }

    public string Version { get; }

    public string ApiVersion { get; } = "v1";

    /// <summary>Last resolved ffmpeg/ffprobe state; refreshed by the hosted service and the diagnostics endpoint.</summary>
    public FfmpegTools Tools { get; set; } = FfmpegTools.Missing;

    public long UptimeSeconds => (long)(DateTime.UtcNow - StartedUtc).TotalSeconds;

    public LanternSettings SettingsNow => Settings.Current;

    public bool TranscodingAvailable => Tools.CanTranscode && Settings.Current.TranscodingEnabled;

    /// <summary>LAN addresses, best first (loopback excluded).</summary>
    public IReadOnlyList<string> LanAddresses() =>
        LanAddressResolver.GetLanAddresses().Select(a => a.ToString()).ToList();

    /// <summary>Address the client should dial back: the one on the interface the request arrived on.</summary>
    public string? PrimaryAddressFor(IPAddress? remoteAddress) =>
        LanAddressResolver.GetPrimaryLanAddress()?.ToString()
        ?? remoteAddress?.ToString();

    /// <summary>Payload used by both discovery transports.</summary>
    public DiscoveryServerDto BuildDiscoveryDto() => new()
    {
        ServerId = Identity.ServerId,
        Name = Settings.Current.ServerName,
        Port = Settings.Current.Port,
        ApiVersion = ApiVersion,
        Version = Version,
        MachineName = Environment.MachineName,
        Host = Identity.HostName,
        Addresses = [.. LanAddresses()],
        RequiresPairing = Settings.Current.RequirePairing,
        UptimeSeconds = UptimeSeconds,
        TranscodingAvailable = TranscodingAvailable,
        StreamingProtocols = Tools.CanTranscode ? ["http-range", "hls"] : ["http-range"],
    };

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit>" suffix the SDK appends.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}

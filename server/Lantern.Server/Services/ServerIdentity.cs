using Lantern.Database;
using Lantern.Server.Data;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Services;

/// <summary>
/// The stable identity of this machine as a Lantern server. It is persisted in the
/// settings table so the Android client keeps recognising the PC across restarts and
/// the mDNS host name never has to change (clients cache it for "last used server").
/// </summary>
public sealed class ServerIdentity
{
    private const string SettingsKey = "server.id";

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<ServerIdentity>? _logger;
    private readonly string? _forcedId;

    public ServerIdentity(SqliteConnectionFactory factory, ILogger<ServerIdentity>? logger = null, string? forcedId = null)
    {
        _factory = factory;
        _logger = logger;
        _forcedId = forcedId;
        EnsureCreated();
    }

    /// <summary>Opaque, stable server identity sent to clients and in discovery packets.</summary>
    public string ServerId { get; private set; } = string.Empty;

    /// <summary>DNS label for the mDNS host record, e.g. <c>lantern-4f2a1c</c>.</summary>
    public string HostLabel => "lantern-" + ServerId[..Math.Min(6, ServerId.Length)].ToLowerInvariant();

    /// <summary>Fully qualified stable host name, e.g. <c>lantern-4f2a1c.local</c>.</summary>
    public string HostName => HostLabel + "." + Lantern.Core.Discovery.DiscoveryConstants.ServiceDomain;

    /// <summary>Turns a display name into a safe DNS-SD instance label.</summary>
    public static string SanitizeLabel(string? value, string fallback = "Lantern")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' or '.') builder.Append(ch);
            else builder.Append('-');
        }

        var label = builder.ToString().Trim(' ', '.', '-');
        if (label.Length == 0) return fallback;
        return label.Length > 63 ? label[..63].TrimEnd(' ', '-') : label;
    }

    private void EnsureCreated()
    {
        using var connection = _factory.Open();

        if (string.IsNullOrWhiteSpace(_forcedId))
        {
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM settings WHERE key = $key";
            read.With("$key", SettingsKey);
            var stored = read.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(stored))
            {
                ServerId = stored.Trim();
                return;
            }
        }

        ServerId = string.IsNullOrWhiteSpace(_forcedId)
            ? Guid.NewGuid().ToString("N")[..12]
            : SanitizeId(_forcedId!);

        using var write = connection.CreateCommand();
        write.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        write.With("$key", SettingsKey).With("$value", ServerId);
        write.ExecuteNonQuery();

        _logger?.LogInformation("Lantern server id {ServerId} ({Host})", ServerId, HostName);
    }

    private static string SanitizeId(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.Length == 0 ? Guid.NewGuid().ToString("N")[..12] : builder.ToString();
    }
}

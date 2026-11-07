using Lantern.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lantern.Database.Repositories;

/// <summary>Persistence for approved ("paired") devices and their capability reports.</summary>
public sealed class DeviceRepository
{
    private readonly SqliteConnectionFactory _factory;

    public DeviceRepository(SqliteConnectionFactory factory) => _factory = factory;

    private const string SelectColumns = """
        id, device_id, name, model, manufacturer, platform, token_hash,
        capabilities_json, approved_utc, last_seen_utc, revoked
        """;

    public async Task<List<TrustedDevice>> GetAllAsync(bool includeRevoked = false, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM trusted_devices"
            + (includeRevoked ? string.Empty : " WHERE revoked = 0")
            + " ORDER BY name COLLATE NOCASE";

        var devices = new List<TrustedDevice>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            devices.Add(Map(reader));
        }
        return devices;
    }

    public async Task<TrustedDevice?> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM trusted_devices WHERE device_id = $id LIMIT 1";
        cmd.AddParam("$id", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Finds the device owning the stored token hash (used by the auth filter).</summary>
    public async Task<TrustedDevice?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM trusted_devices WHERE token_hash = $hash LIMIT 1";
        cmd.AddParam("$hash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Creates or replaces the device row for an approved pairing request.</summary>
    public async Task<long> UpsertAsync(TrustedDevice device, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO trusted_devices
                (device_id, name, model, manufacturer, platform, token_hash,
                 capabilities_json, approved_utc, last_seen_utc, revoked)
            VALUES
                ($deviceId, $name, $model, $manufacturer, $platform, $tokenHash,
                 $capabilities, $approved, $lastSeen, 0)
            ON CONFLICT (device_id) DO UPDATE SET
                name = excluded.name,
                model = excluded.model,
                manufacturer = excluded.manufacturer,
                platform = excluded.platform,
                token_hash = excluded.token_hash,
                capabilities_json = excluded.capabilities_json,
                revoked = 0
            RETURNING id;
            """;
        cmd.AddParam("$deviceId", device.DeviceId)
            .AddParam("$name", device.Name)
            .AddParam("$model", device.Model)
            .AddParam("$manufacturer", device.Manufacturer)
            .AddParam("$platform", device.Platform)
            .AddParam("$tokenHash", device.TokenHash)
            .AddParam("$capabilities", device.CapabilitiesJson)
            .AddParam("$approved", (device.ApprovedUtc == default ? DateTime.UtcNow : device.ApprovedUtc).ToIso())
            .AddParam("$lastSeen", device.LastSeenUtc.ToIsoOrNull());

        device.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return device.Id;
    }

    public async Task SaveCapabilitiesAsync(
        string deviceId, string? capabilitiesJson, DateTime lastSeenUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE trusted_devices
            SET capabilities_json = $capabilities, last_seen_utc = $seen
            WHERE device_id = $id;
            """;
        cmd.AddParam("$capabilities", capabilitiesJson)
            .AddParam("$seen", lastSeenUtc.ToIso())
            .AddParam("$id", deviceId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task TouchLastSeenAsync(string deviceId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE trusted_devices SET last_seen_utc = $seen WHERE device_id = $id";
        cmd.AddParam("$seen", DateTime.UtcNow.ToIso()).AddParam("$id", deviceId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetRevokedAsync(long id, bool revoked, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE trusted_devices SET revoked = $revoked WHERE id = $id";
        cmd.AddParam("$revoked", revoked).AddParam("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM trusted_devices WHERE id = $id";
        cmd.AddParam("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static TrustedDevice Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        DeviceId = r.GetStringOrNull("device_id") ?? string.Empty,
        Name = r.GetStringOrNull("name") ?? "Unknown device",
        Model = r.GetStringOrNull("model"),
        Manufacturer = r.GetStringOrNull("manufacturer"),
        Platform = r.GetStringOrNull("platform"),
        TokenHash = r.GetStringOrNull("token_hash") ?? string.Empty,
        CapabilitiesJson = r.GetStringOrNull("capabilities_json"),
        ApprovedUtc = r.GetDateTime("approved_utc"),
        LastSeenUtc = r.GetDateTimeOrNull("last_seen_utc"),
        Revoked = r.GetBool("revoked"),
    };
}

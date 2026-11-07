using System.Text.Json;
using Lantern.Core.Api;
using Lantern.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lantern.Database;

/// <summary>Creates the SQLite file and applies the schema, then exposes settings access.</summary>
public sealed class DatabaseInitializer
{
    private const string SettingsKey = "server.settings";

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<DatabaseInitializer>? _log;

    public DatabaseInitializer(SqliteConnectionFactory factory, ILogger<DatabaseInitializer>? log = null)
    {
        _factory = factory;
        _log = log;
    }

    public string DatabasePath => _factory.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = Schema.CreateScript;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO schema_info (key, value) VALUES ('version', $version)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """;
            cmd.AddParam("$version", Schema.CurrentVersion.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _log?.LogInformation("Lantern database ready at {Path} (schema v{Version})",
            DatabasePath, Schema.CurrentVersion);
    }

    public int GetSchemaVersion()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = 'version'";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : int.TryParse(value.ToString(), out var v) ? v : 0;
    }

    /// <summary>Loads persisted settings, falling back to defaults for a fresh database.</summary>
    public LanternSettings LoadSettings()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.AddParam("$key", SettingsKey);
        var json = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LanternSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<LanternSettings>(json, ApiJson.Options) ?? new LanternSettings();
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "Stored settings are unreadable; falling back to defaults");
            return new LanternSettings();
        }
    }

    public void SaveSettings(LanternSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, ApiJson.Options);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        cmd.AddParam("$key", SettingsKey).AddParam("$value", json);
        cmd.ExecuteNonQuery();
    }
}

using Lantern.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lantern.Database.Repositories;

/// <summary>
/// Resume positions, keyed by (media, client). The server owns this state so a
/// reinstall or a second device still resumes where the user left off.
/// </summary>
public sealed class ProgressRepository
{
    private readonly SqliteConnectionFactory _factory;

    public ProgressRepository(SqliteConnectionFactory factory) => _factory = factory;

    private const string SelectColumns = """
        id, media_id, client_id, position_ms, duration_ms, state, playback_rate,
        completed, explicit_reset, updated_utc
        """;

    /// <summary>Reads the stored position for a client, falling back to the "any client" row.</summary>
    public async Task<PlaybackProgressRecord?> GetAsync(long mediaId, string clientId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM playback_progress
            WHERE media_id = $media AND (client_id = $client OR client_id = '')
            ORDER BY CASE WHEN client_id = $client THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        cmd.AddParam("$media", mediaId).AddParam("$client", clientId ?? string.Empty);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<List<PlaybackProgressRecord>> GetForClientAsync(
        string clientId, int limit = 50, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns} FROM playback_progress
            WHERE client_id = $client OR client_id = ''
            ORDER BY updated_utc DESC LIMIT $limit;
            """;
        cmd.AddParam("$client", clientId ?? string.Empty).AddParam("$limit", Math.Clamp(limit, 1, 500));

        var rows = new List<PlaybackProgressRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Map(reader));
        }
        return rows;
    }

    /// <summary>Writes a position report. Completed rows keep their 100% position.</summary>
    public async Task<PlaybackProgressRecord> SaveAsync(PlaybackProgressRecord record, CancellationToken ct = default)
    {
        record.UpdatedUtc = DateTime.UtcNow;
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playback_progress
                (media_id, client_id, position_ms, duration_ms, state, playback_rate,
                 completed, explicit_reset, updated_utc)
            VALUES
                ($media, $client, $position, $duration, $state, $rate, $completed, $reset, $updated)
            ON CONFLICT (media_id, client_id) DO UPDATE SET
                position_ms = excluded.position_ms,
                duration_ms = excluded.duration_ms,
                state = excluded.state,
                playback_rate = excluded.playback_rate,
                completed = excluded.completed,
                explicit_reset = excluded.explicit_reset,
                updated_utc = excluded.updated_utc
            RETURNING id;
            """;
        cmd.AddParam("$media", record.MediaId)
            .AddParam("$client", record.ClientId ?? string.Empty)
            .AddParam("$position", record.PositionMs)
            .AddParam("$duration", record.DurationMs)
            .AddParam("$state", (int)record.State)
            .AddParam("$rate", record.PlaybackRate)
            .AddParam("$completed", record.Completed)
            .AddParam("$reset", record.ExplicitReset)
            .AddParam("$updated", record.UpdatedUtc.ToIso());

        record.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return record;
    }

    /// <summary>Deletes the stored position so the next play starts from zero.</summary>
    public async Task ResetAsync(long mediaId, string clientId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM playback_progress WHERE media_id = $media AND (client_id = $client OR client_id = '')";
        cmd.AddParam("$media", mediaId).AddParam("$client", clientId ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Flags (or clears) the watched state of an item for every client.</summary>
    public async Task MarkCompletedAsync(long mediaId, bool completed, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE playback_progress SET completed = $completed WHERE media_id = $media";
        cmd.AddParam("$completed", completed).AddParam("$media", mediaId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }


    private static PlaybackProgressRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        MediaId = r.GetInt64("media_id"),
        ClientId = r.GetStringOrNull("client_id") ?? string.Empty,
        PositionMs = r.GetInt64("position_ms"),
        DurationMs = r.GetInt64("duration_ms"),
        State = (PlaybackState)r.GetInt32("state"),
        PlaybackRate = r.GetSingle("playback_rate"),
        Completed = r.GetBool("completed"),
        ExplicitReset = r.GetBool("explicit_reset"),
        UpdatedUtc = r.GetDateTime("updated_utc"),
    };
}

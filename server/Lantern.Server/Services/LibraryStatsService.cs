using Lantern.Database;

namespace Lantern.Server.Services;

/// <summary>Item/byte totals of one library folder.</summary>
public readonly record struct FolderTotals(int Items, long Bytes);

/// <summary>
/// Per folder aggregates that the repositories do not cover (a single grouped query
/// instead of one COUNT per folder).
/// </summary>
public sealed class LibraryStatsService
{
    private readonly SqliteConnectionFactory _factory;

    public LibraryStatsService(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<Dictionary<long, FolderTotals>> GetFolderTotalsAsync(CancellationToken ct = default)
    {
        var totals = new Dictionary<long, FolderTotals>();

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.folder_id AS folder_id,
                   COUNT(DISTINCT m.id) AS items,
                   COALESCE(SUM(f.size_bytes), 0) AS bytes
            FROM media_items m
            LEFT JOIN media_files f ON f.media_id = m.id
            WHERE m.folder_id IS NOT NULL
            GROUP BY m.folder_id;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            totals[reader.GetInt64(0)] = new FolderTotals(
                Convert.ToInt32(reader.GetInt64(1)),
                reader.GetInt64(2));
        }

        return totals;
    }
}

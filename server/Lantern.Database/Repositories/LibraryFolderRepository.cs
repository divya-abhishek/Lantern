using Lantern.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lantern.Database.Repositories;

/// <summary>CRUD for the folders the scanner walks.</summary>
public sealed class LibraryFolderRepository
{
    private readonly SqliteConnectionFactory _factory;

    public LibraryFolderRepository(SqliteConnectionFactory factory) => _factory = factory;

    private const string SelectColumns = """
        id, path, display_name, recursive, enabled, type, watch_for_changes,
        added_utc, last_scan_utc, last_scan_file_count
        """;

    public async Task<List<LibraryFolder>> GetAllAsync(bool enabledOnly = false, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM library_folders"
            + (enabledOnly ? " WHERE enabled = 1" : string.Empty)
            + " ORDER BY path";

        var results = new List<LibraryFolder>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }
        return results;
    }

    public async Task<LibraryFolder?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM library_folders WHERE id = $id";
        cmd.AddParam("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Adds a folder, or returns the existing id when the path is already registered.</summary>
    public async Task<long> AddAsync(LibraryFolder folder, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO library_folders
                (path, display_name, recursive, enabled, type, watch_for_changes, added_utc, last_scan_utc, last_scan_file_count)
            VALUES
                ($path, $display, $recursive, $enabled, $type, $watch, $added, $lastScan, $count)
            ON CONFLICT (path) DO UPDATE SET
                display_name = excluded.display_name,
                recursive = excluded.recursive,
                enabled = excluded.enabled,
                type = excluded.type,
                watch_for_changes = excluded.watch_for_changes
            RETURNING id;
            """;
        cmd.AddParam("$path", Path.GetFullPath(folder.Path))
            .AddParam("$display", folder.DisplayName)
            .AddParam("$recursive", folder.Recursive)
            .AddParam("$enabled", folder.Enabled)
            .AddParam("$type", (int)folder.Type)
            .AddParam("$watch", folder.WatchForChanges)
            .AddParam("$added", (folder.AddedUtc == default ? DateTime.UtcNow : folder.AddedUtc).ToIso())
            .AddParam("$lastScan", folder.LastScanUtc.ToIsoOrNull())
            .AddParam("$count", folder.LastScanFileCount);

        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        folder.Id = id;
        folder.Path = Path.GetFullPath(folder.Path);
        return id;
    }

    public async Task UpdateAsync(LibraryFolder folder, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE library_folders SET
                display_name = $display,
                recursive = $recursive,
                enabled = $enabled,
                type = $type,
                watch_for_changes = $watch
            WHERE id = $id;
            """;
        cmd.AddParam("$display", folder.DisplayName)
            .AddParam("$recursive", folder.Recursive)
            .AddParam("$enabled", folder.Enabled)
            .AddParam("$type", (int)folder.Type)
            .AddParam("$watch", folder.WatchForChanges)
            .AddParam("$id", folder.Id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkScannedAsync(long id, int fileCount, DateTime scannedUtc, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE library_folders
            SET last_scan_utc = $scan, last_scan_file_count = $count
            WHERE id = $id;
            """;
        cmd.AddParam("$scan", scannedUtc.ToIso()).AddParam("$count", fileCount).AddParam("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the folder together with its media items and files.</summary>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var files = connection.CreateCommand())
        {
            files.Transaction = transaction;
            files.CommandText =
                "DELETE FROM media_files WHERE media_id IN (SELECT id FROM media_items WHERE folder_id = $id);";
            files.AddParam("$id", id);
            await files.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var items = connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = "DELETE FROM media_items WHERE folder_id = $id";
            items.AddParam("$id", id);
            await items.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var folder = connection.CreateCommand())
        {
            folder.Transaction = transaction;
            folder.CommandText = "DELETE FROM library_folders WHERE id = $id";
            folder.AddParam("$id", id);
            await folder.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static LibraryFolder Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        Path = r.GetStringOrNull("path") ?? string.Empty,
        DisplayName = r.GetStringOrNull("display_name"),
        Recursive = r.GetBool("recursive"),
        Enabled = r.GetBool("enabled"),
        Type = r.GetEnum("type", LibraryFolderType.Auto),
        WatchForChanges = r.GetBool("watch_for_changes"),
        AddedUtc = r.GetDateTime("added_utc"),
        LastScanUtc = r.GetDateTimeOrNull("last_scan_utc"),
        LastScanFileCount = r.GetInt32("last_scan_file_count"),
    };
}

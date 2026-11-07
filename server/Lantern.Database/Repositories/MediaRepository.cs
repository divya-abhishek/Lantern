using System.Text;
using Lantern.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lantern.Database.Repositories;

/// <summary>
/// Reads/writes logical media items. The files of an item are handled by the
/// <c>MediaRepository.Files</c> partial so each file stays focused.
/// </summary>
public sealed partial class MediaRepository
{
    private readonly SqliteConnectionFactory _factory;

    public MediaRepository(SqliteConnectionFactory factory) => _factory = factory;

    internal const string ItemColumns = """
        id, title, sort_title, year, kind, show_title, season_number, episode_number,
        episode_title, episode_code, description, folder_id, relative_path, primary_file_id,
        added_utc, updated_utc, last_played_utc, play_count
        """;

    internal static MediaItem MapItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        Title = r.GetStringOrNull("title") ?? string.Empty,
        SortTitle = r.GetStringOrNull("sort_title"),
        Year = r.GetInt32OrNull("year"),
        Kind = r.GetEnum("kind", MediaKind.Movie),
        ShowTitle = r.GetStringOrNull("show_title"),
        SeasonNumber = r.GetInt32OrNull("season_number"),
        EpisodeNumber = r.GetInt32OrNull("episode_number"),
        EpisodeTitle = r.GetStringOrNull("episode_title"),
        EpisodeCode = r.GetStringOrNull("episode_code"),
        Description = r.GetStringOrNull("description"),
        FolderId = r.GetInt64OrNull("folder_id"),
        RelativePath = r.GetStringOrNull("relative_path"),
        PrimaryFileId = r.GetInt64("primary_file_id"),
        AddedUtc = r.GetDateTime("added_utc"),
        UpdatedUtc = r.GetDateTime("updated_utc"),
        LastPlayedUtc = r.GetDateTimeOrNull("last_played_utc"),
        PlayCount = r.GetInt32("play_count"),
    };

    /// <summary>Paged, filtered library query.</summary>
    public async Task<MediaPage> GetPageAsync(MediaQuery query, CancellationToken ct = default)
    {
        query.Limit = Math.Clamp(query.Limit, 1, MediaQuery.MaxLimit);
        var (where, parameters) = BuildFilter(query);

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        int total;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM media_items {where}";
            foreach (var (name, value) in parameters)
            {
                countCmd.AddParam(name, value);
            }
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        var items = new List<MediaItem>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {ItemColumns} FROM media_items
                {where}
                ORDER BY {OrderBy(query.Sort)}
                LIMIT $limit OFFSET $offset;
                """;
            foreach (var (name, value) in parameters)
            {
                cmd.AddParam(name, value);
            }
            cmd.AddParam("$limit", query.Limit).AddParam("$offset", Math.Max(0, query.Offset));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(MapItem(reader));
            }
        }

        if (query.IncludeFiles && items.Count > 0)
        {
            foreach (var item in items)
            {
                item.Files = await GetFilesAsync(connection, item.Id, ct).ConfigureAwait(false);
                if (item.PrimaryFileId == 0 && item.Files.Count > 0)
                {
                    item.PrimaryFileId = item.Files[0].Id;
                }
            }
        }

        return new MediaPage { Items = items, Total = total };
    }

    /// <summary>Loads a single item together with all of its files.</summary>
    public async Task<MediaItem?> GetItemAsync(long id, bool includeFiles = true, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        MediaItem? item = null;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ItemColumns} FROM media_items WHERE id = $id";
            cmd.AddParam("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                item = MapItem(reader);
            }
        }

        if (item is null || !includeFiles)
        {
            return item;
        }

        item.Files = await GetFilesAsync(connection, item.Id, ct).ConfigureAwait(false);
        if (item.PrimaryFileId == 0 && item.Files.Count > 0)
        {
            item.PrimaryFileId = item.Files[0].Id;
        }
        return item;
    }

    /// <summary>Used by the scanner to spot an existing item for a (folder, relative path) pair.</summary>
    public async Task<MediaItem?> FindItemAsync(long folderId, string relativePath, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ItemColumns} FROM media_items
            WHERE folder_id = $folder AND relative_path = $relative COLLATE NOCASE
            LIMIT 1;
            """;
        cmd.AddParam("$folder", folderId).AddParam("$relative", relativePath);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapItem(reader) : null;
    }

    public async Task<long> InsertItemAsync(MediaItem item, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (item.AddedUtc == default) item.AddedUtc = now;
        item.UpdatedUtc = now;

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO media_items
                (title, sort_title, year, kind, show_title, season_number, episode_number,
                 episode_title, episode_code, description, folder_id, relative_path,
                 primary_file_id, added_utc, updated_utc, last_played_utc, play_count)
            VALUES
                ($title, $sort, $year, $kind, $show, $season, $episode, $episodeTitle, $code,
                 $description, $folder, $relative, 0, $added, $updated, $lastPlayed, $playCount)
            RETURNING id;
            """;
        AddItemParams(cmd, item);
        item.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return item.Id;
    }

    public async Task UpdateItemAsync(MediaItem item, CancellationToken ct = default)
    {
        item.UpdatedUtc = DateTime.UtcNow;
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE media_items SET
                title = $title, sort_title = $sort, year = $year, kind = $kind,
                show_title = $show, season_number = $season, episode_number = $episode,
                episode_title = $episodeTitle, episode_code = $code, description = $description,
                folder_id = $folder, relative_path = $relative, primary_file_id = $primary,
                updated_utc = $updated, last_played_utc = $lastPlayed, play_count = $playCount
            WHERE id = $id;
            """;
        AddItemParams(cmd, item);
        cmd.AddParam("$primary", item.PrimaryFileId).AddParam("$id", item.Id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddItemParams(SqliteCommand cmd, MediaItem item)
    {
        cmd.AddParam("$title", item.Title)
            .AddParam("$sort", item.SortTitle ?? item.Title)
            .AddParam("$year", item.Year)
            .AddParam("$kind", (int)item.Kind)
            .AddParam("$show", item.ShowTitle)
            .AddParam("$season", item.SeasonNumber)
            .AddParam("$episode", item.EpisodeNumber)
            .AddParam("$episodeTitle", item.EpisodeTitle)
            .AddParam("$code", item.EpisodeCode)
            .AddParam("$description", item.Description)
            .AddParam("$folder", item.FolderId)
            .AddParam("$relative", item.RelativePath)
            .AddParam("$added", item.AddedUtc.ToIso())
            .AddParam("$updated", item.UpdatedUtc.ToIso())
            .AddParam("$lastPlayed", item.LastPlayedUtc.ToIsoOrNull())
            .AddParam("$playCount", item.PlayCount);
    }

    private static (string Where, List<(string Name, object? Value)> Parameters) BuildFilter(MediaQuery q)
    {
        var sb = new StringBuilder();
        var args = new List<(string, object?)>();

        void And(string clause) => sb.Append(sb.Length == 0 ? " WHERE " : " AND ").Append(clause);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            And("(title LIKE $search OR show_title LIKE $search OR episode_code LIKE $search OR relative_path LIKE $search)");
            args.Add(("$search", $"%{q.Search.Trim()}%"));
        }
        if (q.Kind is not null)
        {
            And("kind = $kind");
            args.Add(("$kind", (int)q.Kind.Value));
        }
        if (q.FolderId is not null)
        {
            And("folder_id = $folder");
            args.Add(("$folder", q.FolderId.Value));
        }
        if (!string.IsNullOrWhiteSpace(q.ShowTitle))
        {
            And("show_title = $show COLLATE NOCASE");
            args.Add(("$show", q.ShowTitle));
        }
        if (q.SeasonNumber is not null)
        {
            And("season_number = $season");
            args.Add(("$season", q.SeasonNumber.Value));
        }
        if (q.MinYear is not null)
        {
            And("year >= $minYear");
            args.Add(("$minYear", q.MinYear.Value));
        }
        if (q.MaxYear is not null)
        {
            And("year <= $maxYear");
            args.Add(("$maxYear", q.MaxYear.Value));
        }
        if (q.Hdr is true)
        {
            And("EXISTS (SELECT 1 FROM media_files f WHERE f.media_id = media_items.id AND f.is_hdr = 1)");
        }
        if (q.Watched is true)
        {
            And("EXISTS (SELECT 1 FROM playback_progress p WHERE p.media_id = media_items.id AND p.completed = 1)");
        }
        else if (q.Watched is false)
        {
            And("NOT EXISTS (SELECT 1 FROM playback_progress p WHERE p.media_id = media_items.id AND p.completed = 1)");
        }

        return (sb.ToString(), args);
    }

    private static string OrderBy(MediaSortOrder sort) => sort switch
    {
        MediaSortOrder.TitleAsc => "sort_title COLLATE NOCASE ASC, year ASC",
        MediaSortOrder.TitleDesc => "sort_title COLLATE NOCASE DESC, year DESC",
        MediaSortOrder.AddedDesc => "added_utc DESC",
        MediaSortOrder.AddedAsc => "added_utc ASC",
        MediaSortOrder.YearDesc => "year DESC, sort_title COLLATE NOCASE ASC",
        MediaSortOrder.DurationDesc =>
            "COALESCE((SELECT MAX(duration_ms) FROM media_files f WHERE f.media_id = media_items.id), 0) DESC",
        MediaSortOrder.SizeDesc =>
            "COALESCE((SELECT MAX(size_bytes) FROM media_files f WHERE f.media_id = media_items.id), 0) DESC",
        MediaSortOrder.LastPlayedDesc => "COALESCE(last_played_utc, '1970-01-01') DESC",
        _ => "sort_title COLLATE NOCASE ASC",
    };

    /// <summary>Removes an item and (via ON DELETE CASCADE) its files and progress rows.</summary>
    public async Task DeleteItemAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM media_items WHERE id = $id";
        cmd.AddParam("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Bumps the play counter and the "last played" timestamp.</summary>
    public async Task TouchPlayedAsync(long mediaId, bool incrementCount, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = incrementCount
            ? "UPDATE media_items SET last_played_utc = $now, play_count = play_count + 1 WHERE id = $id"
            : "UPDATE media_items SET last_played_utc = $now WHERE id = $id";
        cmd.AddParam("$now", DateTime.UtcNow.ToIso()).AddParam("$id", mediaId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetPrimaryFileAsync(long mediaId, long fileId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE media_items SET primary_file_id = $file WHERE id = $id";
        cmd.AddParam("$file", fileId).AddParam("$id", mediaId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every file path currently registered for a folder – used to detect deleted files.</summary>
    public async Task<Dictionary<string, long>> GetFilePathsAsync(long? folderId = null, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = folderId is null
            ? "SELECT f.path, f.id FROM media_files f"
            : """
                SELECT f.path, f.id FROM media_files f
                JOIN media_items m ON m.id = f.media_id
                WHERE m.folder_id = $folder;
                """;
        if (folderId is not null)
        {
            cmd.AddParam("$folder", folderId.Value);
        }

        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var path = reader.GetStringOrNull("path");
            if (!string.IsNullOrEmpty(path))
            {
                map[path] = reader.GetInt64("id");
            }
        }
        return map;
    }
}

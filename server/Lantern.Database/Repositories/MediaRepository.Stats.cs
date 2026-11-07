using Lantern.Core.Dtos;
using Lantern.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lantern.Database.Repositories;

/// <summary>Aggregate/derived queries: counts, resume shelf, recently added, shows.</summary>
public sealed partial class MediaRepository
{
    /// <summary>Library totals for the home screen and the diagnostics page.</summary>
    public async Task<LibraryCountsDto> GetCountsAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var counts = new LibraryCountsDto();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    SUM(CASE WHEN kind = {(int)MediaKind.Movie} THEN 1 ELSE 0 END)   AS movies,
                    SUM(CASE WHEN kind = {(int)MediaKind.Episode} THEN 1 ELSE 0 END) AS episodes,
                    COUNT(DISTINCT CASE WHEN show_title IS NOT NULL THEN show_title END) AS shows,
                    COUNT(*) AS total
                FROM media_items;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                counts.Movies = reader.GetInt32("movies");
                counts.Episodes = reader.GetInt32("episodes");
                counts.Shows = reader.GetInt32("shows");
                counts.TotalItems = reader.GetInt32("total");
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM library_folders) AS folders,
                    (SELECT COALESCE(SUM(size_bytes), 0) FROM media_files) AS bytes,
                    (SELECT MAX(last_scan_utc) FROM library_folders) AS last_scan;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                counts.Folders = reader.GetInt32("folders");
                counts.TotalBytes = reader.GetInt64("bytes");
                counts.LastScanUtc = reader.GetDateTimeOrNull("last_scan");
            }
        }

        return counts;
    }

    /// <summary>Items started but not finished, most recently watched first.</summary>
    public async Task<List<MediaItem>> GetContinueWatchingAsync(
        string? clientId = null, int limit = 20, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ItemColumns} FROM media_items m
            WHERE EXISTS (
                SELECT 1 FROM playback_progress p
                WHERE p.media_id = m.id
                  AND p.completed = 0
                  AND p.position_ms > 15000
                  AND ($client IS NULL OR p.client_id = $client OR p.client_id = '')
            )
            ORDER BY COALESCE(last_played_utc, '1970-01-01') DESC
            LIMIT $limit;
            """;
        cmd.AddParam("$client", string.IsNullOrEmpty(clientId) ? null : clientId)
            .AddParam("$limit", Math.Clamp(limit, 1, 200));

        var items = new List<MediaItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(MapItem(reader));
        }
        return items;
    }

    public async Task<List<MediaItem>> GetRecentlyAddedAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {ItemColumns} FROM media_items ORDER BY added_utc DESC LIMIT $limit";
        cmd.AddParam("$limit", Math.Clamp(limit, 1, 200));

        var items = new List<MediaItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(MapItem(reader));
        }
        return items;
    }

    /// <summary>One row per show with its season list, for the TV browser.</summary>
    public async Task<List<ShowGroupDto>> GetShowsAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT show_title,
                   COUNT(*) AS episodes,
                   MIN(season_number) AS first_season,
                   MAX(season_number) AS last_season
            FROM media_items
            WHERE show_title IS NOT NULL AND show_title <> ''
            GROUP BY show_title
            ORDER BY show_title COLLATE NOCASE;
            """;

        var shows = new List<ShowGroupDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var title = reader.GetStringOrNull("show_title") ?? "Unknown Show";
                var first = reader.GetInt32OrNull("first_season") ?? 1;
                var last = reader.GetInt32OrNull("last_season") ?? first;
                var seasons = new List<int>();
                for (var s = Math.Min(first, last); s <= Math.Max(first, last); s++)
                {
                    seasons.Add(s);
                }

                shows.Add(new ShowGroupDto
                {
                    ShowTitle = title,
                    EpisodeCount = reader.GetInt32("episodes"),
                    Seasons = seasons,
                });
            }
        }

        foreach (var show in shows)
        {
            show.Latest = await GetLatestEpisodeAsync(connection, show.ShowTitle, ct).ConfigureAwait(false);
        }
        return shows;
    }

    private async Task<MediaSummaryDto?> GetLatestEpisodeAsync(
        SqliteConnection connection, string showTitle, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ItemColumns} FROM media_items
            WHERE show_title = $show COLLATE NOCASE
            ORDER BY added_utc DESC LIMIT 1;
            """;
        cmd.AddParam("$show", showTitle);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var item = MapItem(reader);
        return new MediaSummaryDto
        {
            Id = item.Id,
            StreamId = item.Id.ToString(),
            Title = item.Title,
            Kind = item.Kind,
            EpisodeCode = item.EpisodeCode,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            ShowTitle = item.ShowTitle,
            Year = item.Year,
            AddedUtc = item.AddedUtc,
        };
    }
}

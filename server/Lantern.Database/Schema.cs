namespace Lantern.Database;

/// <summary>
/// The full database DDL. Kept as a single script so a fresh install is created
/// in one transaction; <see cref="DatabaseInitializer"/> records the applied
/// version in the "schema_info" table for future migrations.
/// </summary>
internal static class Schema
{
    public const int CurrentVersion = 1;

    public const string CreateScript = """
        CREATE TABLE IF NOT EXISTS schema_info (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS library_folders (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            path                 TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            display_name         TEXT    NULL,
            recursive            INTEGER NOT NULL DEFAULT 1,
            enabled              INTEGER NOT NULL DEFAULT 1,
            type                 INTEGER NOT NULL DEFAULT 0,
            watch_for_changes    INTEGER NOT NULL DEFAULT 1,
            added_utc            TEXT    NOT NULL,
            last_scan_utc        TEXT    NULL,
            last_scan_file_count INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS media_items (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            title          TEXT    NOT NULL,
            sort_title     TEXT    NULL,
            year           INTEGER NULL,
            kind           INTEGER NOT NULL DEFAULT 0,
            show_title     TEXT    NULL,
            season_number  INTEGER NULL,
            episode_number INTEGER NULL,
            episode_title  TEXT    NULL,
            episode_code   TEXT    NULL,
            description    TEXT    NULL,
            folder_id      INTEGER NULL REFERENCES library_folders (id) ON DELETE SET NULL,
            relative_path  TEXT    NULL,
            primary_file_id INTEGER NOT NULL DEFAULT 0,
            added_utc      TEXT    NOT NULL,
            updated_utc    TEXT    NOT NULL,
            last_played_utc TEXT   NULL,
            play_count     INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_media_items_kind        ON media_items (kind);
        CREATE INDEX IF NOT EXISTS ix_media_items_sort_title  ON media_items (sort_title);
        CREATE INDEX IF NOT EXISTS ix_media_items_sort_year   ON media_items (sort_title, year);
        CREATE INDEX IF NOT EXISTS ix_media_items_show        ON media_items (show_title, season_number, episode_number);
        CREATE TABLE IF NOT EXISTS media_files (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            media_id           INTEGER NOT NULL REFERENCES media_items (id) ON DELETE CASCADE,
            path               TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            container          TEXT    NULL,
            size_bytes         INTEGER NOT NULL DEFAULT 0,
            duration_ms        INTEGER NOT NULL DEFAULT 0,
            video_codec        TEXT    NULL,
            audio_codec        TEXT    NULL,
            width              INTEGER NULL,
            height             INTEGER NULL,
            frame_rate         REAL    NULL,
            bitrate_bps        INTEGER NULL,
            pixel_format       TEXT    NULL,
            is_hdr             INTEGER NOT NULL DEFAULT 0,
            dv_profile         TEXT    NULL,
            audio_stream_count INTEGER NOT NULL DEFAULT 0,
            audio_tracks_json  TEXT    NULL,
            subtitle_tracks_json TEXT  NULL,
            last_write_utc     TEXT    NOT NULL,
            last_scanned_utc   TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_media_files_media ON media_files (media_id);

        CREATE TABLE IF NOT EXISTS playback_progress (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            media_id       INTEGER NOT NULL REFERENCES media_items (id) ON DELETE CASCADE,
            client_id      TEXT    NOT NULL,
            position_ms    INTEGER NOT NULL DEFAULT 0,
            duration_ms    INTEGER NOT NULL DEFAULT 0,
            state          INTEGER NOT NULL DEFAULT 0,
            playback_rate  REAL    NOT NULL DEFAULT 1,
            completed      INTEGER NOT NULL DEFAULT 0,
            explicit_reset INTEGER NULL,
            updated_utc    TEXT    NOT NULL,
            UNIQUE (media_id, client_id)
        );

        CREATE INDEX IF NOT EXISTS ix_progress_updated ON playback_progress (updated_utc DESC);

        CREATE TABLE IF NOT EXISTS trusted_devices (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id         TEXT    NOT NULL UNIQUE,
            name              TEXT    NOT NULL,
            model             TEXT    NULL,
            manufacturer      TEXT    NULL,
            platform          TEXT    NULL,
            token_hash        TEXT    NOT NULL,
            capabilities_json TEXT    NULL,
            approved_utc      TEXT    NOT NULL,
            last_seen_utc     TEXT    NULL,
            revoked           INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}


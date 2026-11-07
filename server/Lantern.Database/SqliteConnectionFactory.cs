using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lantern.Database;

/// <summary>
/// Creates SQLite connections with the pragmas Lantern needs (WAL for concurrent
/// reads while the scanner writes, foreign keys for cascading deletes).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly ILogger<SqliteConnectionFactory>? _log;
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath, ILogger<SqliteConnectionFactory>? log = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _log = log;
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaSql;
        cmd.ExecuteNonQuery();
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmaSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string PragmaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA busy_timeout = 10000;
        PRAGMA cache_size = -20000;
        PRAGMA foreign_keys = ON;
        """;
}

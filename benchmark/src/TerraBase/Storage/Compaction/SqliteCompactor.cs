using Microsoft.Data.Sqlite;
using TerraBase.Storage.Schema;
using System.Diagnostics;

namespace TerraBase.Storage.Compaction;

public sealed record SqliteCompactionOptions
{
    public bool RebuildPages { get; init; } = true;
    public bool TruncateWal { get; init; } = true;
    public int BusyTimeoutMilliseconds { get; init; } = SqliteSchema.BusyTimeoutMilliseconds;
}

public sealed record SqliteCompactionResult(long FreelistPages, long PageCount, long JournalBytes, TimeSpan Duration);

/// <summary>Runs bounded online SQLite maintenance without blocking readers.</summary>
public static class SqliteCompactor
{
    public static async Task<SqliteCompactionResult> CompactAsync(string databasePath, SqliteCompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        options ??= new SqliteCompactionOptions();
        var started = Stopwatch.GetTimestamp();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private,
            ForeignKeys = true, DefaultTimeout = Math.Max(1, options.BusyTimeoutMilliseconds / 1000)
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteSchema.ConfigureConnection(connection);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = options.TruncateWal ? "PRAGMA wal_checkpoint(TRUNCATE);" : "PRAGMA wal_checkpoint(PASSIVE);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (options.RebuildPages)
        {
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var optimize = connection.CreateCommand())
        {
            optimize.CommandText = "PRAGMA optimize;";
            await optimize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var (freelist, pages) = await ReadStatsAsync(connection, cancellationToken).ConfigureAwait(false);
        var wal = File.Exists(databasePath + "-wal") ? new FileInfo(databasePath + "-wal").Length : 0;
        return new SqliteCompactionResult(freelist, pages, wal, Stopwatch.GetElapsedTime(started));
    }

    private static async Task<(long Freelist, long Pages)> ReadStatsAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT freelist_count FROM pragma_freelist_count), (SELECT page_count FROM pragma_page_count);";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return (0, 0);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}

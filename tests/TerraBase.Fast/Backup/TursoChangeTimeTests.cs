using System.Globalization;
using TerraBase.Engine.Sqlite;
using Turso;

namespace TerraBase.Fast.Backup;

/// <summary>
/// What a point-in-time restore relies on from Turso's change capture, measured on the real driver with the capture mode
/// TerraBase uses: every captured row, the commit row included, holds change_time, the wall clock's Unix time when Turso
/// wrote that row, rounded down to the second. The clock is the machine's; nothing here controls it.
/// </summary>
public sealed class TursoChangeTimeTests
{
    [Test]
    public async Task Each_captured_row_and_the_commit_row_hold_the_wall_clock_second_they_were_written_in_rounded_down(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-change-time-");
        try
        {
            using var connection = new TursoConnection($"Data Source={Path.Combine(directory.FullName, "measure.db")}");
            connection.Open();
            Execute(connection, SqliteSession.CaptureOn, "CREATE TABLE measure(x);");
            var after = (long)Scalar(connection, "SELECT max(change_id) FROM turso_cdc;")!;

            // Late in a second, where rounding to the nearest second would give the next one, and ending in the second it
            // started (started over when the machine stalls across the boundary).
            DateTimeOffset start, end;
            while (true)
            {
                Execute(connection, "BEGIN;");
                await DelayAsync(Next(TimeProvider.System.GetUtcNow()).AddMilliseconds(600), cancellationToken);
                start = TimeProvider.System.GetUtcNow();
                Execute(connection, "INSERT INTO measure VALUES (1);");
                end = TimeProvider.System.GetUtcNow();
                if (start.ToUnixTimeSeconds() == end.ToUnixTimeSeconds())
                {
                    break;
                }

                Execute(connection, "ROLLBACK;");
            }

            // The same transaction's second statement, and its commit, in a later second.
            await DelayAsync(Next(end), cancellationToken);
            var later = TimeProvider.System.GetUtcNow();
            Execute(connection, "INSERT INTO measure VALUES (2);", "COMMIT;");
            var committed = TimeProvider.System.GetUtcNow();

            var rows = Rows(connection, after);
            await Assert.That(rows.Select(row => row.Type).ToArray()).IsEquivalentTo([1L, 1L, 2L]);
            await Assert.That(rows[0].Time).IsEqualTo(start.ToUnixTimeSeconds());
            foreach (var (_, time) in rows.Skip(1))
            {
                await Assert.That(time).IsGreaterThanOrEqualTo(later.ToUnixTimeSeconds()).And.IsLessThanOrEqualTo(committed.ToUnixTimeSeconds());
            }

            await Assert.That(rows[1].Time).IsGreaterThan(rows[0].Time);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static DateTimeOffset Next(DateTimeOffset time) => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds() + 1);

    // A timer can fire a little before the wall clock reaches the time.
    private static async Task DelayAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        while (until - TimeProvider.System.GetUtcNow() is { Ticks: > 0 } wait)
        {
            await Task.Delay(wait + TimeSpan.FromMilliseconds(1), cancellationToken);
        }
    }

    private static List<(long Type, long Time)> Rows(TursoConnection connection, long after)
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"SELECT change_type, change_time FROM turso_cdc WHERE change_id > {after} ORDER BY change_id;");
        using var reader = command.ExecuteReader();
        List<(long, long)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    private static object? Scalar(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(TursoConnection connection, params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}

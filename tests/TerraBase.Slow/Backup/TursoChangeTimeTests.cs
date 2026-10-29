using System.Globalization;
using TerraBase.Engine.Turso;
using Turso;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Backup;

/// <summary>
/// What a point-in-time restore relies on from Turso's change capture, measured on the real driver with the capture mode
/// TerraBase uses: every captured row, the commit row included, holds change_time, the wall clock's Unix time when Turso
/// wrote that row, rounded down to the second; and a commit that returned on the writer's connection is visible to the
/// next read on another connection, as the backup tap's read mark assumes. The clock is the machine's; nothing here
/// controls it.
/// </summary>
public sealed class TursoChangeTimeTests
{
    // Commits the writer makes while the reader checks.
    private const int Commits = 3000;

    [Test]
    public async Task A_commit_returned_on_the_writer_is_in_the_next_newest_commit_read_on_another_connection(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-read-visibility-");
        try
        {
            var path = Path.Combine(directory.FullName, "visible.db");
            // The connections a store's writer and the backup tap's reader use, with their settings.
            using var writer = new SqliteConnection($"Data Source={path}");
            writer.Open();
            Execute(writer, "PRAGMA synchronous = NORMAL;", TursoSession.CaptureOn, "CREATE TABLE visible(x);");
            using var reader = new TursoConnection($"Data Source={path}");
            TursoSession.OpenExisting(reader, path);

            // The newest commit row of a transaction whose commit has returned, published only after it returned.
            long returned = 0;
            var commits = Task.Run(() =>
            {
                for (var commit = 0; commit < Commits; commit++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (commit % 2 == 0)
                    {
                        using var transaction = writer.BeginTransaction();
                        using var insert = writer.CreateCommand();
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO visible VALUES (1);";
                        insert.ExecuteNonQuery();
                        transaction.Commit();
                    }
                    else
                    {
                        // An autocommit statement commits as it returns.
                        Execute(writer, "INSERT INTO visible VALUES (2);");
                    }

                    Volatile.Write(ref returned, (long)Scalar(writer, Newest)!);
                }
            }, cancellationToken);

            // The tap's order: take the time (here, what has returned by then), then query the newest commit.
            var (checks, missed) = (0, 0);
            while (!commits.IsCompleted)
            {
                var before = Volatile.Read(ref returned);
                if ((Scalar(reader, Newest) as long? ?? 0) < before)
                {
                    missed++;
                }

                checks++;
            }

            await commits;
            TestContext.Current!.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture, $"commits={Commits} checks={checks} missed={missed}"));
            await Assert.That(missed).IsEqualTo(0);
            await Assert.That(checks).IsGreaterThan(Commits / 10);
            await Assert.That(Scalar(reader, Newest)).IsEqualTo(Volatile.Read(ref returned));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Each_captured_row_and_the_commit_row_hold_the_wall_clock_second_they_were_written_in_rounded_down(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-change-time-");
        try
        {
            using var connection = new TursoConnection($"Data Source={Path.Combine(directory.FullName, "measure.db")}");
            connection.Open();
            Execute(connection, TursoSession.CaptureOn, "CREATE TABLE measure(x);");
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

    private const string Newest = "SELECT max(change_id) FROM turso_cdc WHERE change_type = 2;";

    private static object? Scalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(System.Data.Common.DbConnection connection, params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}

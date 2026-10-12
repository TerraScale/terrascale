using Turso.Data.Sqlite;
using TerraBase.Engine.Sqlite;

namespace TerraBase.Fast.Storage;

// The engine runs every statement without result columns through TursoStatements, which disposes it even when it fails:
// Turso.Data.Sqlite 0.8.2 (SqliteCommand.Execute) leaves such a failed statement holding its connection until the garbage
// collector finalizes it, on the finalizer thread.
public sealed class TursoStatementsTests
{
    [Test]
    public async Task Failed_statements_leave_nothing_for_the_finalizer_thread_to_finalize()
    {
        using var directory = new TemporaryDirectory();
        using var connection = Open(directory);
        connection.Run("CREATE TABLE t (id TEXT NOT NULL UNIQUE); INSERT INTO t VALUES ('a');");
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT count(*) FROM t;";

        // Young collections all the while: a statement left to the garbage collector is finalized on the finalizer thread
        // while this thread steps the next one, which Turso then refuses ("concurrent use forbidden").
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(0);
                GC.WaitForPendingFinalizers();
            }
        });
        var refused = new List<string>();
        try
        {
            for (var attempt = 0; attempt < 5_000; attempt++)
            {
                try
                {
                    connection.Run("INSERT INTO t VALUES ('a');");
                    refused.Add("a duplicate was inserted");
                }
                catch (SqliteException error) when (error.Message.Contains("UNIQUE", StringComparison.Ordinal))
                {
                }
                catch (SqliteException error)
                {
                    refused.Add(error.Message);
                }

                try
                {
                    _ = read.ExecuteScalar();
                }
                catch (SqliteException error)
                {
                    refused.Add(error.Message);
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await collector;
        }

        await Assert.That(refused).IsEmpty();
    }

    [Test]
    public async Task A_transaction_survives_a_failed_write_and_reports_a_stopped_read_that_ended_it()
    {
        using var directory = new TemporaryDirectory();
        using var connection = Open(directory);
        connection.Run("CREATE TABLE t (id TEXT NOT NULL UNIQUE);");
        using var session = new SqliteSession(connection);

        // A constraint error fails only its statement: the transaction and its savepoint go on.
        using (var transaction = TursoTransaction.Begin(connection, deferred: false))
        {
            connection.Run("INSERT INTO t VALUES ('kept');");
            transaction.Save("batched");
            _ = Assert.Throws<SqliteException>(() => connection.Run("INSERT INTO t VALUES ('kept');"));
            await Assert.That(session.InTransaction()).IsTrue();
            transaction.Rollback("batched");
            transaction.Release("batched");
            transaction.Commit();
        }

        // A read that fails while it steps ends the whole transaction (Turso); the probe says so, and its write is gone.
        connection.CreateFunction<object?, bool>("stop_here", _ => throw new OperationCanceledException("the test stops this read"));
        var aborted = TursoTransaction.Begin(connection, deferred: false);
        connection.Run("INSERT INTO t VALUES ('lost');");
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id FROM t WHERE stop_here(id);";
            _ = Assert.Throws<SqliteException>(() =>
            {
                using var rows = read.ExecuteReader();
                while (rows.Read())
                {
                }
            });
        }

        await Assert.That(session.InTransaction()).IsFalse();
        var ended = Assert.Throws<SqliteException>(aborted.Dispose);
        await Assert.That(SqliteSession.NoTransaction(ended!)).IsTrue();
        await Assert.That(Count(connection)).IsEqualTo(1L);
        connection.Run("INSERT INTO t VALUES ('after');");
        await Assert.That(Count(connection)).IsEqualTo(2L);
    }

    private static SqliteConnection Open(TemporaryDirectory directory)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "t.db")}");
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection)
    {
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t;";
        return (long)count.ExecuteScalar()!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("turso-statements-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

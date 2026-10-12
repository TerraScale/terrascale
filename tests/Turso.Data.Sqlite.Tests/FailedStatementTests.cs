namespace Turso.Data.Sqlite.Tests;

/// <summary>
/// A failed statement must be finalized by the call that ran it. One the driver leaves to the garbage collector is
/// finalized later on the finalizer thread: a second user of the connection, which Turso refuses with
/// "concurrent use forbidden" when it meets the owner stepping another statement.
/// </summary>
public sealed class FailedStatementTests
{
    private const int Attempts = 5_000;

    /// <summary>A write that fails while it steps (a UNIQUE violation) is finalized by ExecuteNonQuery itself.</summary>
    [Test]
    public async Task AFailedWriteLeavesNothingForTheFinalizer()
    {
        using var database = new TemporaryDatabase();
        using var write = database.Connection.CreateCommand();
        write.CommandText = "INSERT INTO t VALUES ('a');";

        var refused = await UnderYoungCollectionsAsync(database, () => write.ExecuteNonQuery());

        await Assert.That(refused).IsEmpty();
    }

    /// <summary>A statement after a result set that fails in NextResult is finalized by the reader itself.</summary>
    [Test]
    public async Task AFailedStatementAfterAResultSetLeavesNothingForTheFinalizer()
    {
        using var database = new TemporaryDatabase();
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT 1; INSERT INTO t VALUES ('a');";

        var refused = await UnderYoungCollectionsAsync(database, () =>
        {
            using var reader = command.ExecuteReader();
            _ = reader.Read();
            _ = reader.NextResult();
        });

        await Assert.That(refused).IsEmpty();
    }

    // Runs the failing call and then a read, Attempts times, while young collections and their finalizers run all the
    // while; returns every error but the expected UNIQUE violation.
    private static async Task<List<string>> UnderYoungCollectionsAsync(TemporaryDatabase database, Action failing)
    {
        using var read = database.Connection.CreateCommand();
        read.CommandText = "SELECT count(*) FROM t;";
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
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    failing();
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

        return refused;
    }
}

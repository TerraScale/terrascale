namespace Turso.Data.Sqlite.Tests;

/// <summary>Abandoning unread native rows releases the connection without running the rest of the batch.</summary>
public sealed class ReaderAbortTests
{
    /// <summary>Closing unread rows does not invoke their failing function, execute later SQL, or end the transaction.</summary>
    [Test]
    public async Task AbortingUnreadRowsPreservesTheTransactionAndDiscardsTheRemainingBatch()
    {
        using var database = new TemporaryDatabase();
        var connection = database.Connection;
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO t VALUES ('b');");
        var cancelled = false;
        connection.CreateFunction<string, bool>("consume", _ => !cancelled
            ? true
            : throw new InvalidOperationException("unread rows must not execute"));
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM t WHERE consume(id); INSERT INTO t VALUES ('unexpected');";
            using var reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            cancelled = true;
            reader.Abort();
            reader.Abort();
            await Assert.That(reader.IsClosed).IsTrue();
        }

        Execute(connection, transaction, "INSERT INTO t VALUES ('c');");
        transaction.Commit();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT id FROM t ORDER BY id;";
        using var rows = read.ExecuteReader();
        var ids = new List<string>();
        while (rows.Read())
        {
            ids.Add(rows.GetString(0));
        }

        await Assert.That(ids).IsEquivalentTo(["a", "b", "c"]);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

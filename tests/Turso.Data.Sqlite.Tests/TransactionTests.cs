namespace Turso.Data.Sqlite.Tests;

/// <summary>
/// Unlike SQLite, Turso rolls the whole transaction back when a statement fails while it steps (a read whose function
/// throws, for one). The transaction object must then know it ended.
/// </summary>
public sealed class TransactionTests
{
    /// <summary>A read that fails while it steps ends the transaction (Turso): the transaction knows, later commands of it fail instead of committing on their own, and disposing it does not throw.</summary>
    [Test]
    public async Task ATransactionAFailedReadEndedIsRolledBackAndRefusesLaterCommands()
    {
        using var database = new TemporaryDatabase();
        var connection = database.Connection;
        connection.CreateFunction<object?, bool>("stop_here", _ => throw new InvalidOperationException("the test stops this read"));
        var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO t VALUES ('lost');");
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id FROM t WHERE stop_here(id);";
            _ = Assert.Throws<SqliteException>(() =>
            {
                using var rows = read.ExecuteReader();
                while (rows.Read())
                {
                }
            });
        }

        // Not run on its own outside the transaction it was meant for.
        _ = Assert.Throws<InvalidOperationException>(() => Execute(connection, transaction, "INSERT INTO t VALUES ('after');"));
        transaction.Dispose();

        await Assert.That(Ids(connection)).IsEquivalentTo(["a"]);
    }

    /// <summary>A UNIQUE violation fails only its statement: the transaction and its savepoints go on.</summary>
    [Test]
    public async Task ATransactionOutlivesAFailedWrite()
    {
        using var database = new TemporaryDatabase();
        var connection = database.Connection;
        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, transaction, "INSERT INTO t VALUES ('kept');");
            transaction.Save("batched");
            _ = Assert.Throws<SqliteException>(() => Execute(connection, transaction, "INSERT INTO t VALUES ('a');"));
            transaction.Rollback("batched");
            transaction.Release("batched");
            Execute(connection, transaction, "INSERT INTO t VALUES ('also kept');");
            transaction.Commit();
        }

        await Assert.That(Ids(connection)).IsEquivalentTo(["a", "also kept", "kept"]);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Ids(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM t ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}

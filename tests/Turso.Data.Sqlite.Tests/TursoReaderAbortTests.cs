namespace Turso.Data.Sqlite.Tests;

/// <summary>The raw reader stops a read-only SELECT without stepping its unread rows.</summary>
public sealed class TursoReaderAbortTests
{
    /// <summary>Aborting after one row runs the filter no further, and the transaction around it still commits.</summary>
    [Test]
    public async Task AbortingUnreadRowsSkipsTheDrainAndKeepsTheTransaction()
    {
        using var database = new TemporaryDatabase();
        var connection = database.Connection;
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO t VALUES ('b'), ('c');");
        var consumed = 0;
        connection.CreateFunction<string, bool>("consume", _ => ++consumed > 0);
        using var native = Turso.Raw.Public.TursoBindings.PrepareStatement(connection.DatabaseHandle, "SELECT id FROM t WHERE consume(id);");
        using var command = new Turso.TursoCommand();
        using (var reader = new Turso.TursoDataReader(command, native, System.Data.CommandBehavior.Default))
        {
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(consumed).IsEqualTo(1);
            // Dispose alone would finalize, and finalize steps the two unread rows through consume.
            reader.Abort();
            reader.Abort();
            await Assert.That(reader.IsClosed).IsTrue();
        }

        await Assert.That(consumed).IsEqualTo(1);
        Execute(connection, transaction, "INSERT INTO t VALUES ('d');");
        transaction.Commit();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t;";
        await Assert.That(count.ExecuteScalar()).IsEqualTo(4L);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

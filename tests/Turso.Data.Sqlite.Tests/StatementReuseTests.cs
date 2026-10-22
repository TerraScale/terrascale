using System.Reflection;
using Turso.Raw.Public.Handles;

namespace Turso.Data.Sqlite.Tests;

/// <summary>Native commands reuse only clean statements on their original open connection.</summary>
public sealed class StatementReuseTests
{
    /// <summary>Explicit preparation survives changing values; missing parameters and cancellation retain validation.</summary>
    [Test]
    public async Task PreparedValuesAreReboundAndMissingParametersAreRejected()
    {
        using var database = new TemporaryDatabase();
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT $value;";
        var value = command.Parameters.AddWithValue("$value", "first");
        command.Prepare();
        var prepared = Cached(command);
        await Assert.That(command.ExecuteScalar()).IsEqualTo("first");
        value.Value = 42L;
        await Assert.That(command.ExecuteScalar()).IsEqualTo(42L);
        await Assert.That(ReferenceEquals(Cached(command), prepared)).IsTrue();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(cancelled.Token));
        command.Parameters.Clear();
        _ = Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
        await Assert.That(prepared!.IsClosed).IsTrue();
        command.Parameters.AddWithValue("$value", DBNull.Value);
        await Assert.That(command.ExecuteScalar()).IsEqualTo(DBNull.Value);
        command.CommandText = "SELECT 1 WHERE 0;";
        await Assert.That(command.ExecuteScalar()).IsNull();
        await Assert.That(command.ExecuteScalar()).IsNull();
        using var empty = command.ExecuteReader();
        await Assert.That(empty.FieldCount).IsEqualTo(1);
        await Assert.That(empty.Read()).IsFalse();
        await Assert.That(empty.Read()).IsFalse();
    }

    /// <summary>Changing text, assigning another connection, and reopening the same connection invalidate its cache.</summary>
    [Test]
    public async Task TextAndNativeConnectionChangesInvalidateTheStatement()
    {
        using var first = new TemporaryDatabase();
        using var second = new TemporaryDatabase();
        using var command = first.Connection.CreateCommand();
        command.CommandText = "SELECT id FROM t;";
        await Assert.That(command.ExecuteScalar()).IsEqualTo("a");
        var previous = Cached(command);
        command.CommandText = "SELECT count(*) FROM t;";
        await Assert.That(previous!.IsClosed).IsTrue();
        await Assert.That(command.ExecuteScalar()).IsEqualTo(1L);
        previous = Cached(command);
        using var insert = second.Connection.CreateCommand();
        insert.CommandText = "INSERT INTO t VALUES ('b');";
        insert.ExecuteNonQuery();
        command.Connection = second.Connection;
        await Assert.That(previous!.IsClosed).IsTrue();
        await Assert.That(command.ExecuteScalar()).IsEqualTo(2L);
        previous = Cached(command);
        second.Connection.Close();
        second.Connection.Open();
        await Assert.That(command.ExecuteScalar()).IsEqualTo(2L);
        await Assert.That(previous!.IsClosed).IsTrue();
        command.CommandText = "SELECT 1; SELECT 2;";
        using var rows = command.ExecuteReader();
        await Assert.That(rows.Read()).IsTrue();
        await Assert.That(rows.GetInt64(0)).IsEqualTo(1);
        await Assert.That(rows.NextResult()).IsTrue();
        await Assert.That(rows.Read()).IsTrue();
        await Assert.That(rows.GetInt64(0)).IsEqualTo(2);
        await Assert.That(Cached(command)).IsNull();
    }

    /// <summary>A failed execution is discarded, and reuse never bypasses a completed transaction.</summary>
    [Test]
    public async Task WriteFailureAndRollbackPreserveTheNextExecution()
    {
        using var database = new TemporaryDatabase();
        using var command = database.Connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES ($id);";
        var id = command.Parameters.AddWithValue("$id", "b");
        await Assert.That(command.ExecuteNonQuery()).IsEqualTo(1);
        var previous = Cached(command);
        _ = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        await Assert.That(previous!.IsClosed).IsTrue();
        await Assert.That(Cached(command)).IsNull();
        id.Value = "c";
        await Assert.That(command.ExecuteNonQuery()).IsEqualTo(1);
        using (var transaction = database.Connection.BeginTransaction())
        {
            command.Transaction = transaction;
            id.Value = "rolled-back";
            command.ExecuteNonQuery();
            transaction.Rollback();
            _ = Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
        }
        using (var transaction = database.Connection.BeginTransaction())
        {
            command.Transaction = transaction;
            id.Value = "committed";
            await Assert.That(command.ExecuteNonQuery()).IsEqualTo(1);
            transaction.Commit();
        }
        command.Transaction = null;
        command.CommandText = "UPDATE t SET id = id WHERE id = $id RETURNING id;";
        await Assert.That(command.ExecuteNonQuery()).IsEqualTo(1);
        id.Value = "c";
        await Assert.That(command.ExecuteNonQuery()).IsEqualTo(1);
        using var count = database.Connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t;";
        await Assert.That(count.ExecuteScalar()).IsEqualTo(4L);
    }

    /// <summary>Reprepare refreshes metadata before Read, abort discards its statement, and a live reader owns its handle.</summary>
    [Test]
    public async Task SchemaChangesAbortAndCommandDisposalRespectReaderOwnership()
    {
        using var database = new TemporaryDatabase();
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT * FROM t;";
        await Assert.That(command.ExecuteScalar()).IsEqualTo("a");
        using (var alter = database.Connection.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE t ADD COLUMN label TEXT DEFAULT 'label';";
            alter.ExecuteNonQuery();
        }
        await Assert.That(command.ExecuteScalar()).IsEqualTo("a");
        using (var rows = command.ExecuteReader())
        {
            await Assert.That(rows.FieldCount).IsEqualTo(2);
            await Assert.That(rows.GetName(1)).IsEqualTo("label");
            await Assert.That(rows.Read()).IsTrue();
            await Assert.That(rows.GetString(1)).IsEqualTo("label");
            rows.Abort();
            await Assert.That(Cached(command)).IsNull();
        }
        using var active = command.ExecuteReader();
        command.Dispose();
        await Assert.That(active.Read()).IsTrue();
        active.Close();
        await Assert.That(Cached(command)).IsNull();
    }

    /// <summary>A public reader neither executes warmed RETURNING early nor rebinds parameters while exposing metadata.</summary>
    [Test]
    public async Task WarmedReturningReadersStayLazyAndKeepTheirBoundValues()
    {
        using var database = new TemporaryDatabase();
        using var command = database.Connection.CreateCommand();
        command.CommandText = "INSERT INTO t(id) VALUES ($id) RETURNING *;";
        var id = command.Parameters.AddWithValue("$id", "warmed");
        await Assert.That(command.ExecuteNonQuery()).IsEqualTo(1);
        var prepared = Cached(command);
        using (var alter = database.Connection.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE t ADD COLUMN label TEXT DEFAULT 'label';";
            alter.ExecuteNonQuery();
        }
        id.Value = "aborted";
        using (var rows = command.ExecuteReader())
        {
            await Assert.That(prepared!.IsClosed).IsTrue();
            await Assert.That(rows.FieldCount).IsEqualTo(2);
            await Assert.That(rows.GetName(1)).IsEqualTo("label");
            rows.Abort();
        }
        id.Value = "captured";
        using (var rows = command.ExecuteReader())
        {
            id.Value = "changed";
            await Assert.That(rows.FieldCount).IsEqualTo(2);
            await Assert.That(rows.GetName(0)).IsEqualTo("id");
            await Assert.That(rows.Read()).IsTrue();
            await Assert.That(rows.GetString(0)).IsEqualTo("captured");
        }
        command.CommandText = "SELECT group_concat(id, ',') FROM (SELECT id FROM t ORDER BY id);";
        await Assert.That(command.ExecuteScalar()).IsEqualTo("a,captured,warmed");
    }

    private static TursoStatementHandle? Cached(SqliteCommand command) =>
        (TursoStatementHandle?)typeof(SqliteCommand).GetField("_statement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(command);
}

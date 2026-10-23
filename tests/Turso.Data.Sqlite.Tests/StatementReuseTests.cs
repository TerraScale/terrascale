using System.Reflection;
using Turso.Raw.Public.Handles;

namespace Turso.Data.Sqlite.Tests;

/// <summary>A command with <see cref="SqliteCommand.ReuseStatement"/> keeps one prepared statement across executions.</summary>
public sealed class StatementReuseTests
{
    /// <summary>The kept statement runs again with new parameters, after a reader left rows unread and across transactions.</summary>
    [Test]
    public async Task AKeptStatementRunsAgainWithNewParameters()
    {
        using var database = new TemporaryDatabase();
        Execute(database.Connection, "INSERT INTO t VALUES ('b'), ('c');");
        using var query = database.Connection.CreateCommand();
        query.ReuseStatement = true;
        query.CommandText = "SELECT id FROM t WHERE id >= $from ORDER BY id;";
        query.Parameters.AddWithValue("$from", "a");
        using (var reader = query.ExecuteReader())
        {
            // Stops after one row: closing resets the statement for its next execution.
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetString(0)).IsEqualTo("a");
        }

        var kept = Kept(query);
        using (var transaction = database.Connection.BeginTransaction())
        {
            query.Transaction = transaction;
            query.Parameters["$from"].Value = "b";
            await Assert.That(string.Join(",", Ids(query))).IsEqualTo("b,c");
            transaction.Commit();
        }

        query.Transaction = null;
        query.Parameters["$from"].Value = "c";
        await Assert.That(string.Join(",", Ids(query))).IsEqualTo("c");
        await Assert.That(Kept(query)).IsSameReferenceAs(kept);
    }

    /// <summary>A failed execution finalizes the kept statement; the next execution prepares a new one and succeeds.</summary>
    [Test]
    public async Task AFailedExecutionDropsTheKeptStatement()
    {
        using var database = new TemporaryDatabase();
        using var insert = database.Connection.CreateCommand();
        insert.ReuseStatement = true;
        insert.CommandText = "INSERT INTO t VALUES ($id);";
        insert.Parameters.AddWithValue("$id", "b");
        await Assert.That(insert.ExecuteNonQuery()).IsEqualTo(1);
        var kept = Kept(insert);
        _ = Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
        await Assert.That(kept.IsClosed).IsTrue();
        insert.Parameters["$id"].Value = "c";
        await Assert.That(insert.ExecuteNonQuery()).IsEqualTo(1);
        await Assert.That(string.Join(",", Ids(database.Connection, "SELECT id FROM t ORDER BY id;"))).IsEqualTo("a,b,c");
    }

    /// <summary>Another connection's schema change between executions: the kept statement reads the new table.</summary>
    [Test]
    public async Task AKeptStatementFollowsASchemaChange()
    {
        using var database = new TemporaryDatabase();
        using var query = database.Connection.CreateCommand();
        query.ReuseStatement = true;
        query.CommandText = "SELECT id FROM t WHERE id = $id;";
        query.Parameters.AddWithValue("$id", "a");
        await Assert.That(query.ExecuteScalar()).IsEqualTo("a");
        using (var other = new SqliteConnection(database.Connection.ConnectionString))
        {
            other.Open();
            Execute(other, "DROP TABLE t; CREATE TABLE t (n INTEGER, id TEXT NOT NULL UNIQUE); INSERT INTO t VALUES (1, 'z'); CREATE INDEX t_n ON t (n);");
        }

        query.Parameters["$id"].Value = "z";
        await Assert.That(query.ExecuteScalar()).IsEqualTo("z");
    }

    /// <summary>
    /// The kept statement's parameter indexes and parsed text are reused: parameters bind by name whatever their order, a
    /// missing one still fails, and new command text (of another parameter layout) is parsed and indexed again.
    /// </summary>
    [Test]
    public async Task AKeptStatementBindsByNameAcrossExecutionsAndTextChanges()
    {
        using var database = new TemporaryDatabase();
        using var upsert = database.Connection.CreateCommand();
        upsert.ReuseStatement = true;
        upsert.CommandText = "INSERT INTO t VALUES ($first || $second);";
        upsert.Parameters.AddWithValue("$first", "b");
        upsert.Parameters.AddWithValue("$second", "1");
        await Assert.That(upsert.ExecuteNonQuery()).IsEqualTo(1);
        var kept = Kept(upsert);
        upsert.Parameters.Clear();
        upsert.Parameters.AddWithValue("$second", "2");
        upsert.Parameters.AddWithValue("$first", "c");
        await Assert.That(upsert.ExecuteNonQuery()).IsEqualTo(1);
        await Assert.That(Kept(upsert)).IsSameReferenceAs(kept);
        upsert.Parameters.Clear();
        upsert.Parameters.AddWithValue("$first", "d");
        _ = Assert.Throws<InvalidOperationException>(() => upsert.ExecuteNonQuery());

        upsert.Parameters.Clear();
        upsert.CommandText = "INSERT INTO t VALUES ($only);";
        upsert.Parameters.AddWithValue("$only", "e");
        await Assert.That(upsert.ExecuteNonQuery()).IsEqualTo(1);
        await Assert.That(string.Join(",", Ids(database.Connection, "SELECT id FROM t ORDER BY id;"))).IsEqualTo("a,b1,c2,e");
    }

    private static TursoStatementHandle Kept(SqliteCommand command) =>
        (TursoStatementHandle)(typeof(SqliteCommand).GetField("_statement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(command)
            ?? throw new InvalidOperationException("the command keeps no statement"));

    private static List<string> Ids(SqliteCommand command)
    {
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static List<string> Ids(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Ids(command);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

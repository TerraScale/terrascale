namespace Turso.Data.Sqlite.Tests;

/// <summary>A command text splits into statements the way SQLite completes them, so a trigger body stays whole.</summary>
public sealed class TriggerStatementTests
{
    /// <summary>Semicolons in a trigger body, its comments and its strings end no statement before the trigger's END.</summary>
    [Test]
    public async Task ATriggerBodyWithSeveralStatementsIsOneStatementOfItsCommandText()
    {
        using var database = new TemporaryDatabase();
        Execute(database.Connection, "CREATE TABLE audit (id TEXT NOT NULL, kind TEXT NOT NULL);");
        Execute(database.Connection, """
            CREATE TRIGGER t_audit AFTER INSERT ON t
            BEGIN
              INSERT INTO audit VALUES (new.id, 'first');
              -- a comment; with a semicolon
              INSERT INTO audit VALUES (new.id, 'second; quoted');
            END
            """);
        Execute(database.Connection, """
            create trigger t_gone after delete on t begin insert into audit values (old.id, 'gone'); end;
            INSERT INTO t VALUES ('b'); DELETE FROM t WHERE id = 'a';
            """);

        using var query = database.Connection.CreateCommand();
        query.CommandText = "SELECT id || ':' || kind FROM audit ORDER BY rowid;";
        var rows = new List<string>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(reader.GetString(0));
            }
        }

        await Assert.That(rows).IsEquivalentTo(["b:first", "b:second; quoted", "a:gone"]);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

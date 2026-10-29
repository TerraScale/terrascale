using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlIndexTests
{
    private static object Insert(int id, string name) => new { sql = "INSERT INTO people (id, name) VALUES (?, ?)", args = new object[] { id, name } };

    [Test]
    public async Task Indexes_are_created_and_dropped_and_a_unique_index_rejects_duplicates()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, team TEXT)");

        var created = await SqlApi.ExecuteAsync(api,
            "CREATE INDEX people_team ON people (team)", "CREATE INDEX IF NOT EXISTS people_team ON people (name)", "CREATE UNIQUE INDEX people_name ON People (Name DESC)");
        var taken = await SqlApi.ExecuteAsync(api, "CREATE INDEX people_team ON people (name)");
        var duplicate = await SqlApi.SendAsync(api, api.Client, Insert(1, "ada"), Insert(2, "ada"));
        var partial = await SqlApi.ExecuteAsync(api, "CREATE INDEX p ON people (team) WHERE team IS NOT NULL");
        var unknown = await SqlApi.ExecuteAsync(api, "CREATE INDEX p ON people (nope)");
        var dropped = await SqlApi.ExecuteAsync(api, "DROP INDEX people_name", "DROP INDEX IF EXISTS people_name", "DROP INDEX people_team");
        var missing = await SqlApi.ExecuteAsync(api, "DROP INDEX people_name");
        var after = await SqlApi.SendAsync(api, api.Client, Insert(1, "ada"), Insert(2, "ada"));
        var rows = await SqlApi.OkAsync(api, "SELECT count(*) FROM people WHERE name = 'ada'");

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SqlApi.Error(taken)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 0));
        await Assert.That(SqlApi.Error(duplicate)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_CONSTRAINT_UNIQUE", 1));
        await Assert.That(SqlApi.Error(partial).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(SqlApi.Error(unknown).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(dropped.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SqlApi.Error(missing).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(after.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(rows[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(2);
    }

    [Test]
    public async Task A_unique_index_on_a_table_with_rows_checks_them()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, team TEXT)");
        await SqlApi.SendAsync(api, api.Client, Insert(1, "ada"), Insert(2, "ada"), Insert(3, "bob"),
            new { sql = "UPDATE people SET team = 't' || id" });

        var duplicates = await SqlApi.ExecuteAsync(api, "CREATE UNIQUE INDEX people_name ON people (name)");
        var distinct = await SqlApi.ExecuteAsync(api, "CREATE UNIQUE INDEX people_team ON people (team)", "CREATE UNIQUE INDEX people_name_team ON people (name, team)");
        var taken = await SqlApi.ExecuteAsync(api, "UPDATE people SET team = 't1' WHERE id = 3");
        var retried = await SqlApi.ExecuteAsync(api, "CREATE UNIQUE INDEX people_name ON people (name)");

        await Assert.That(SqlApi.Error(duplicates)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_CONSTRAINT_UNIQUE", 0));
        await Assert.That(distinct.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SqlApi.Error(taken).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
        await Assert.That(SqlApi.Error(retried).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
    }

}

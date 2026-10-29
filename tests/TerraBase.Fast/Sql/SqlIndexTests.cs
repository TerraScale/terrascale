using System.Net;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Hrana;

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
        var inserted = await SqlApi.SendAsync(api, api.Client, Insert(1, "ada"), Insert(2, "ada"), Insert(3, "bob"),
            new { sql = "UPDATE people SET team = 't' || id" });
        await Assert.That(inserted.Status).IsEqualTo(HttpStatusCode.OK).Because(inserted.Message ?? "index fixture");

        var duplicates = await SqlApi.ExecuteAsync(api, "CREATE UNIQUE INDEX people_name ON people (name)");
        var distinct = await SqlApi.ExecuteAsync(api, "CREATE UNIQUE INDEX people_team ON people (team)", "CREATE UNIQUE INDEX people_name_team ON people (name, team)");
        var taken = await SqlApi.ExecuteAsync(api, "UPDATE people SET team = 't1' WHERE id = 3");
        var retried = await SqlApi.ExecuteAsync(api, "CREATE UNIQUE INDEX people_name ON people (name)");

        await Assert.That(SqlApi.Error(duplicates)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_CONSTRAINT_UNIQUE", 0));
        await Assert.That(distinct.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(SqlApi.Error(taken).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
        await Assert.That(SqlApi.Error(retried).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
    }

    [Test]
    public async Task Hrana_runs_index_statements()
    {
        await using var api = await DocumentApi.StartAsync();
        var body = await HranaPipelineTests.PostAsync(api, """
            {"requests":[{"type":"execute","stmt":{"sql":"CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)"}},
             {"type":"execute","stmt":{"sql":"CREATE INDEX people_id_name ON people (id, name)"}},
             {"type":"execute","stmt":{"sql":"CREATE UNIQUE INDEX people_name ON people (name)"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO people VALUES (1, 'ada')"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO people VALUES (2, 'ada')"}},
             {"type":"execute","stmt":{"sql":"DROP INDEX people_name"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO people VALUES (2, 'ada')"}},
             {"type":"close"}]}
            """);

        var results = body.Json.GetProperty("results");
        await Assert.That(HranaPipelineTests.Types(body.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:execute,error,ok:execute,ok:execute,ok:close");
        await Assert.That(results[4].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_CONSTRAINT_UNIQUE");
    }
}

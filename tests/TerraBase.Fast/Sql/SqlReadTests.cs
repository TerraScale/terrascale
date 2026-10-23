using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlReadTests
{
    private static readonly int[] TwoArguments = [1, 2];

    // Rows are documents: seeded through the document API, key columns stored as fields too.
    private static async Task<DocumentApi> SeedAsync()
    {
        var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, team TEXT, age INTEGER, score REAL)",
            "CREATE TABLE teams (code TEXT PRIMARY KEY, title TEXT)");
        (await api.TransactAsync(
            Put("people", "1", new { id = 1, name = "ann", team = "a", age = 31, score = 1.5 }),
            Put("people", "2", new { id = 2, name = "bob", team = "a", age = 25, score = 2.5 }),
            Put("people", "3", new { id = 3, name = "cy", team = "b", age = 40 }),
            Put("teams", "a", new { code = "a", title = "Alpha" }),
            Put("teams", "b", new { code = "b", title = "Beta" }))).Status.ShouldBe(HttpStatusCode.OK);
        return api;
    }

    private static object Put(string collection, string id, object document) => new { op = "put", collection, id, document };

    private static async Task<string> RowsAsync(DocumentApi api, object statement)
    {
        var response = await SqlApi.SendAsync(api, api.Client, statement);
        response.Status.ShouldBe(HttpStatusCode.OK);
        return response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText();
    }

    [Test]
    public async Task Select_reads_joins_aggregates_ctes_windows_and_values_with_parameters()
    {
        await using var api = await SeedAsync();

        (object Statement, string Rows)[] cases =
        [
            (new { sql = "SELECT p.name, t.title FROM people p JOIN teams t ON t.code = p.team WHERE p.age > ? ORDER BY p.name", args = new object[] { 30 } },
                """[["ann","Alpha"],["cy","Beta"]]"""),
            (new { sql = "SELECT team, count(*) AS n, sum(age) FROM people GROUP BY team HAVING count(*) > :min ORDER BY team", named = new { min = 1 } },
                """[["a",2,56]]"""),
            (new { sql = "WITH older AS (SELECT * FROM people WHERE age >= $a) SELECT name, score FROM older ORDER BY id", named = new { a = 30 } },
                """[["ann",1.5],["cy",null]]"""),
            (new { sql = "SELECT name, row_number() OVER (ORDER BY age DESC) FROM people ORDER BY 2" }, """[["cy",1],["ann",2],["bob",3]]"""),
            (new { sql = "SELECT name FROM people WHERE id = ?2 AND name = ?1", args = new object[] { "bob", 2 } }, """[["bob"]]"""),
            // SQLite numbering: :x is 1, ? is 2; positional arguments bind named parameters by number.
            (new { sql = "SELECT :x, ?, :x", args = new object[] { 5, "six" } }, """[[5,"six",5]]"""),
            (new { sql = "SELECT @x, $y", named = new Dictionary<string, object?> { ["@x"] = true, ["y"] = null } }, """[[1,null]]"""),
            (new { sql = "VALUES (1, 'a'), (2.5, NULL)" }, """[[1,"a"],[2.5,null]]"""),
            (new { sql = "SELECT name FROM people WHERE team IN (SELECT code FROM teams WHERE title LIKE 'B%') AND EXISTS (SELECT 1 FROM teams)" }, """[["cy"]]"""),
            (new { sql = "SELECT json_extract(json_object('n', count(*)), '$.n'), upper(min(name)) FROM people" }, """[[3,"ANN"]]"""),
            (new { sql = "SELECT name FROM people UNION SELECT title FROM teams ORDER BY 1 LIMIT 2 OFFSET 1" }, """[["Beta"],["ann"]]"""),
        ];

        foreach (var (statement, rows) in cases)
        {
            await Assert.That(await RowsAsync(api, statement)).IsEqualTo(rows).Because(JsonSerializer.Serialize(statement));
        }
    }

    [Test]
    public async Task Reads_outside_sql_tables_functions_and_bounds_are_refused()
    {
        await using var api = await SeedAsync();
        string[] refused =
        [
            "SELECT * FROM items",
            "SELECT * FROM main.people",
            "SELECT * FROM sqlite_master",
            "SELECT * FROM json_each('[1]')",
            "SELECT load_extension('secret_lib')",
            "SELECT tb_qos_scan(1)",
            "SELECT randomblob(4)",
            "SELECT x'00'",
            "SELECT main.people.id FROM people",
            "SELECT secret_column FROM people",
            "SELECT * FROM (WITH c AS (SELECT 1) SELECT * FROM c)",
            "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c",
            "SELECT * FROM people FOR UPDATE",
        ];

        foreach (var sql in refused)
        {
            var response = await SqlApi.ExecuteAsync(api, sql);
            await Assert.That(SqlApi.Error(response)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 0)).Because(sql);
            await Assert.That(response.Message!).DoesNotContain("secret").Because(sql);
        }

        var extraArgument = await SqlApi.SendAsync(api, api.Client, new { sql = "SELECT ?", args = TwoArguments });
        await Assert.That(SqlApi.Error(extraArgument).Sqlite).IsEqualTo("SQLITE_ERROR");
    }

    [Test]
    public async Task Results_past_10000_rows_or_8_mib_are_too_big()
    {
        await using var api = await SeedAsync();
        // Ten thousand rows from a cross join of VALUES; the cap holds exactly at the limit.
        const string digits = "(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))";
        var exact = await SqlApi.ExecuteAsync(api, $"SELECT 1 FROM {digits} a, {digits} b, {digits} c, {digits} d");
        var rows = await SqlApi.ExecuteAsync(api, $"SELECT 1 FROM {digits} a, {digits} b, {digits} c, {digits} d, (VALUES (0),(1)) e");
        // 600 rows of 16 KiB: past 8 MiB.
        var wide = await SqlApi.ExecuteAsync(api, $"SELECT substr(json_object('k', printf('%016384d', 0)), 1, 16384) FROM {digits} a, {digits} b, (VALUES (0),(1),(2),(3),(4),(5)) c");

        await Assert.That(exact.Status).IsEqualTo(HttpStatusCode.OK).Because(exact.Code + " " + exact.Message);
        await Assert.That(exact.Json.GetProperty("results")[0].GetProperty("rows").GetArrayLength()).IsEqualTo(10_000);
        await Assert.That(SqlApi.Error(rows)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 0));
        await Assert.That(SqlApi.Error(wide)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 0));
    }

    [Test]
    public async Task A_scan_past_the_work_budget_is_interrupted()
    {
        await using var api = await SeedAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE big (id INTEGER PRIMARY KEY, v INTEGER)");
        // More rows than a Hobby read may scan (10,000), stored straight in the collection's table.
        await using (var connection = new Turso.Data.Sqlite.SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 12000)
                INSERT INTO "big" (id, doc) SELECT i, json_object('id', i, 'v', i) FROM n;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var expensive = await SqlApi.ExecuteAsync(api, "SELECT count(*) FROM big");
        var bounded = await SqlApi.ExecuteAsync(api, "SELECT count(*) FROM people");

        await Assert.That(SqlApi.Error(expensive)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_INTERRUPT", 0));
        await Assert.That(bounded.Status).IsEqualTo(HttpStatusCode.OK);
    }
}

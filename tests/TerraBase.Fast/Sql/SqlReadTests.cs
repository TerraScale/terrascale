using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Sql;

namespace TerraBase.Fast.Sql;

public sealed class SqlReadTests
{
    private static readonly int[] TwoArguments = [1, 2];

    // Rows are documents keyed by their primary key, seeded through SQL.
    private static async Task<DocumentApi> SeedAsync()
    {
        var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, team TEXT, age INTEGER, score REAL)",
            "CREATE TABLE teams (code TEXT PRIMARY KEY, title TEXT)",
            "INSERT INTO people VALUES (1, 'ann', 'a', 31, 1.5), (2, 'bob', 'a', 25, 2.5)",
            "INSERT INTO people (id, name, team, age) VALUES (3, 'cy', 'b', 40)",
            "INSERT INTO teams VALUES ('a', 'Alpha'), ('b', 'Beta')");
        return api;
    }

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
    public async Task Columns_report_names_and_declared_types_and_describe_lists_parameters_in_sqlite_order()
    {
        await using var api = await SeedAsync();
        var all = (await SqlApi.OkAsync(api, "SELECT * FROM people WHERE id = 1"))[0];
        var mixed = (await SqlApi.OkAsync(api, "SELECT p.name AS who, count(*) AS n, t.* FROM people p JOIN teams t ON t.code = p.team GROUP BY p.name ORDER BY 1 LIMIT 1"))[0];
        await using var session = SqlHarness.Open(api, canWrite: false);
        var described = await session.DescribeAsync(SqlHarness.Command("SELECT name AS n FROM people WHERE id = :id AND age > ? AND name = ?5 OR :id IS NULL"), CancellationToken.None);
        var ddl = await session.DescribeAsync(SqlHarness.Command("CREATE TABLE x (a TEXT)"), CancellationToken.None);

        await Assert.That(all.GetProperty("columns").GetRawText()).IsEqualTo(
            """[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"},{"name":"team","decltype":"TEXT"},{"name":"age","decltype":"INTEGER"},{"name":"score","decltype":"REAL"}]""");
        await Assert.That(all.GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"ann","a",31,1.5]]""");
        await Assert.That(mixed.GetProperty("columns").GetRawText()).IsEqualTo(
            """[{"name":"who","decltype":"TEXT"},{"name":"n","decltype":null},{"name":"code","decltype":"TEXT"},{"name":"title","decltype":"TEXT"}]""");
        var description = described is SqlDescription value ? value : throw new InvalidOperationException("describe failed");
        await Assert.That(description.Params).IsEquivalentTo([":id", null, null, null, "?5"]);
        await Assert.That(description.Columns.Select(column => column.Name)).IsEquivalentTo(["n"]);
        await Assert.That(description.IsReadOnly).IsTrue();
        await Assert.That(ddl is SqlDescription { Columns.Count: 0, IsReadOnly: false }).IsTrue();
    }

    [Test]
    public async Task Arity_is_the_highest_parameter_number_and_tables_and_error_kinds_are_listed()
    {
        await using var api = await SeedAsync();
        await using var session = SqlHarness.Open(api, canWrite: false);
        var service = api.Server.Services.GetRequiredService<ISqlService>();

        // PostgreSQL clients send $2 as ?2 with two arguments even when $1 is unused.
        var second = await RowsAsync(api, new { sql = "SELECT ?2", args = new object[] { "unused", "b" } });
        var described = await session.DescribeAsync(SqlHarness.Command("SELECT ?2"), CancellationToken.None);
        var tables = await service.ListTablesAsync(await api.TenantOfAsync(), CancellationToken.None);
        var missingTable = await session.ExecuteAsync(SqlHarness.Command("SELECT * FROM missing_table"), CancellationToken.None);
        var missingColumn = await session.ExecuteAsync(SqlHarness.Command("SELECT missing_column FROM people"), CancellationToken.None);
        var syntax = await session.ExecuteAsync(SqlHarness.Command("SELECT FROM WHERE"), CancellationToken.None);

        await Assert.That(second).IsEqualTo("""[["b"]]""");
        await Assert.That(described is SqlDescription { Params: [null, "?2"] }).IsTrue();
        await Assert.That(tables.Select(table => table.Name)).IsEquivalentTo(["people", "teams"]);
        await Assert.That(tables[0].Columns[0]).IsEqualTo(new SqlTableColumn("id", "INTEGER", NotNull: true, PrimaryKey: true));
        await Assert.That(tables[0].Columns[4]).IsEqualTo(new SqlTableColumn("score", "REAL", NotNull: false, PrimaryKey: false));
        await Assert.That(missingTable is SqlError { Kind: "no_such_table" }).IsTrue();
        await Assert.That(missingColumn is SqlError { Kind: "no_such_column" }).IsTrue();
        await Assert.That(syntax is SqlError { Kind: "syntax" }).IsTrue();
    }

    // As in SQLite, a comparison with a column applies the column's affinity to the constant side.
    [Test]
    public async Task Comparisons_apply_column_affinity_to_literals_and_parameters_and_tables_report_defaults()
    {
        await using var api = await SeedAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE d (k TEXT PRIMARY KEY, n INTEGER DEFAULT -1, label TEXT DEFAULT 'it''s')", "INSERT INTO d (k) VALUES (7)");

        (object Statement, string Rows)[] cases =
        [
            (new { sql = "SELECT name FROM people WHERE id = '2'" }, """[["bob"]]"""),
            (new { sql = "SELECT name FROM people WHERE '3' = id" }, """[["cy"]]"""),
            (new { sql = "SELECT name FROM people WHERE id IN ('1', '2') ORDER BY id" }, """[["ann"],["bob"]]"""),
            (new { sql = "SELECT name FROM people WHERE id = ?", args = new object[] { "1" } }, """[["ann"]]"""),
            (new { sql = "SELECT k FROM d WHERE k = 7" }, """[["7"]]"""),
            (new { sql = "SELECT name FROM people WHERE score > '2'" }, """[["bob"]]"""),
        ];

        foreach (var (statement, rows) in cases)
        {
            await Assert.That(await RowsAsync(api, statement)).IsEqualTo(rows).Because(JsonSerializer.Serialize(statement));
        }

        var table = (await api.Server.Services.GetRequiredService<ISqlService>().ListTablesAsync(await api.TenantOfAsync(), CancellationToken.None)).Single(item => item.Name == "d");
        await Assert.That(table.Columns.Select(column => column.Default)).IsEquivalentTo([null, "-1", "'it''s'"]);
    }

    // Affinity is applied per comparison, in that comparison's scope: one parameter can meet columns of other affinities.
    [Test]
    public async Task Comparisons_coerce_each_occurrence_by_the_column_in_its_own_scope()
    {
        await using var api = await SeedAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE q (n INTEGER, s TEXT)", "CREATE TABLE r (s INTEGER)", "CREATE TABLE kv (k TEXT PRIMARY KEY, v TEXT)",
            "INSERT INTO q VALUES (1, '01')", "INSERT INTO r VALUES (1)", "INSERT INTO kv VALUES ('7', 'sev')");

        var x = new { x = "01" };
        (object Statement, string Rows)[] cases =
        [
            (new { sql = "SELECT n FROM q WHERE n = :x AND s = :x", named = x }, """[[1]]"""),
            (new { sql = "SELECT n FROM q WHERE s = :x AND n IN (:x)", named = x }, """[[1]]"""),
            (new { sql = "SELECT q.n FROM q JOIN r ON 1 = 1 WHERE r.s = :x AND q.s = :x", named = x }, """[[1]]"""),
            (new { sql = "SELECT a.n FROM q a, r b WHERE b.s = '01' AND a.s = '01'" }, """[[1]]"""),
            (new { sql = "WITH c AS (SELECT n AS m, s FROM q) SELECT m FROM c WHERE m = :x AND s = :x", named = x }, """[[1]]"""),
            (new { sql = "WITH c (m, t) AS (SELECT n, s FROM q) SELECT m FROM c WHERE m = :x AND t = :x", named = x }, """[[1]]"""),
            (new { sql = "SELECT t.n FROM (SELECT * FROM q) t WHERE t.n = :x AND t.s = :x", named = x }, """[[1]]"""),
            (new { sql = "SELECT n FROM q WHERE EXISTS (SELECT 1 FROM r WHERE s = :x) AND s = :x", named = x }, """[[1]]"""),
            (new { sql = "SELECT n FROM q WHERE s = :x AND n = (SELECT s FROM r WHERE s = :x)", named = x }, """[[1]]"""),
            (new { sql = "SELECT v FROM kv WHERE k = ?", args = new object[] { 7 } }, """[["sev"]]"""),
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
            "SELECT * FROM temp.people",
            "SELECT * FROM sqlite_sequence",
            "SELECT * FROM json_each('[1]')",
            "SELECT load_extension('secret_lib')",
            "SELECT tb_qos_scan(1)",
            "SELECT randomblob(4)",
            "SELECT x'00'",
            "SELECT other.people.id FROM people",
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

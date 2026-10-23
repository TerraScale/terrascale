using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseSqlExecuteTests
{
    private static readonly string Route = $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/sql/execute";

    [Test]
    public async Task A_query_sends_positional_and_named_arguments_and_reads_typed_rows()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(Route);
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            bodies.Add(await request.Content.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json("""
                {"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"},{"name":"score"},{"name":"note","decltype":null}],
                "rows":[[9007199254740993,"Ada",1.5,null],[2,"Bob",0,"x"]],"rows_affected":0,"last_insert_rowid":null}]}
                """);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var rows = await db.QuerySqlAsync("SELECT * FROM people WHERE id > ? AND active = ? AND name <> ? AND note IS ?", [1, true, "Zed", null]);
        await db.QuerySqlAsync("SELECT * FROM people WHERE name = :name AND score > @min", new Dictionary<string, TerraBaseSqlValue> { ["name"] = "Ada", ["min"] = 0.25 });

        await Assert.That(bodies[0]).IsEqualTo("""{"statements":[{"sql":"SELECT * FROM people WHERE id > ? AND active = ? AND name <> ? AND note IS ?","args":[1,true,"Zed",null]}]}""");
        await Assert.That(bodies[1]).IsEqualTo("""{"statements":[{"sql":"SELECT * FROM people WHERE name = :name AND score > @min","named":{"name":"Ada","min":0.25}}]}""");
        await Assert.That(rows.Count).IsEqualTo(2);
        var first = rows[0];
        await Assert.That(first.Columns[0]).IsEqualTo(new TerraBaseSqlColumn("id", "INTEGER"));
        await Assert.That(first.Columns[2].Decltype).IsNull();
        await Assert.That(first.GetInt64("id")).IsEqualTo(9007199254740993L);
        await Assert.That(first.GetString(1)).IsEqualTo("Ada");
        await Assert.That(first.GetDouble("score")).IsEqualTo(1.5);
        await Assert.That(first.IsNull("note")).IsTrue();
        await Assert.That(rows[1].GetBoolean("score")).IsFalse();
        await Assert.That(rows[1].ToDictionary()["note"].GetString()).IsEqualTo("x");
        await Assert.That(() => first.GetString("id")).Throws<InvalidOperationException>();
        await Assert.That(() => first.GetOrdinal("missing")).Throws<ArgumentException>();
    }

    [Test]
    public async Task A_batch_is_one_request_with_one_result_per_statement()
    {
        string? body = null;
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            body = await request.Content!.ReadAsStringAsync(token);
            return TerraBaseIntegrationFixture.Json("""
                {"results":[{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null},
                {"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":7}]}
                """);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var results = await db.ExecuteSqlBatchAsync([
            new("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)"),
            new("INSERT INTO people (name) VALUES ($name)", Named: new Dictionary<string, TerraBaseSqlValue> { ["name"] = "Ada" }),
        ]);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(body).IsEqualTo("""{"statements":[{"sql":"CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)"},{"sql":"INSERT INTO people (name) VALUES ($name)","named":{"name":"Ada"}}]}""");
        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[1].RowsAffected).IsEqualTo(1);
        await Assert.That(results[1].LastInsertRowid).IsEqualTo(7);
        await Assert.That(results[0].LastInsertRowid).IsNull();
    }

    [Test]
    [Arguments(400, "sql_error", "SQLITE_CONSTRAINT_PRIMARYKEY")]
    [Arguments(403, "forbidden_scope", "SQLITE_AUTH")]
    [Arguments(409, "sql_conflict", "SQLITE_BUSY")]
    [Arguments(413, "sql_too_large", "SQLITE_TOOBIG")]
    public async Task A_refused_statement_throws_a_sql_exception_once(int status, string code, string sqliteCode)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json(
                $$$"""{"error":{"code":"{{{code}}}","message":"refused","statement":1,"sqlite_code":"{{{sqliteCode}}}"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var error = await Assert.ThrowsAsync<TerraBaseSqlException>(() => db.ExecuteSqlBatchAsync([new("SELECT 1"), new("INSERT INTO people VALUES (1)")]));

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That((int)error!.StatusCode!).IsEqualTo(status);
        await Assert.That(error.Code).IsEqualTo(code);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(error.Statement).IsEqualTo(1);
        await Assert.That(error.SqliteCode).IsEqualTo(sqliteCode);
        await Assert.That(error.Message).IsEqualTo("refused");
    }

    [Test]
    [Arguments(403, "forbidden_scope")]
    [Arguments(400, "invalid_request")]
    public async Task Generic_errors_keep_their_existing_exception_types(int status, string code)
    {
        using var handler = new TerraBaseTestHandler((_, _) =>
            Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused","op":0}}""", (HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.ExecuteSqlAsync("SELECT 1"));

        await Assert.That(error).IsNotTypeOf<TerraBaseSqlException>();
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(error.OperationIndex).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"results":[]}""")]
    [Arguments("""{"results":[{"columns":[{"name":"a"}],"rows":[[1,2]],"rows_affected":0}]}""")]
    [Arguments("""{"results":[{"columns":[{"name":"a"}],"rows":[[[1]]],"rows_affected":0}]}""")]
    public async Task Malformed_results_are_invalid_responses(string response)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(response)));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.ExecuteSqlAsync("SELECT 1"));

        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task Invalid_statements_are_rejected_before_any_request()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException($"request {++calls} sent"));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");

        await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteSqlBatchAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteSqlBatchAsync([.. Enumerable.Repeat(new TerraBaseSqlStatement("SELECT 1"), 257)]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteSqlAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteSqlAsync("SELECT :a", new Dictionary<string, TerraBaseSqlValue> { [":a"] = 1 }));
        await Assert.That(() => (TerraBaseSqlValue)double.NaN).Throws<ArgumentOutOfRangeException>();
        await Assert.That(calls).IsEqualTo(0);
    }
}

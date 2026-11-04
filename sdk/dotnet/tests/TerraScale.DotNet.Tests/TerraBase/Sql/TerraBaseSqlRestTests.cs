using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed partial class TerraBaseSqlRestTests
{
    private static readonly string Route = $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/sql/execute";

    internal sealed record Person(long Id, string Name, double? Score, byte[]? Avatar);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Person))]
    internal sealed partial class PersonJson : JsonSerializerContext;

    internal static TerraBaseSqlDatabase Database(HttpClient http) =>
        new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = "sjc", AccessTokenProvider = _ => ValueTask.FromResult("project-user-token") })
            .Database(TerraBaseIntegrationFixture.ProjectId, "app");

    [Test]
    public async Task A_query_sends_positional_named_and_blob_arguments_and_reads_rows()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(Route);
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            await Assert.That(request.Headers.GetValues(TerraBaseRegion.PinHeader).Single()).IsEqualTo("sjc");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            bodies.Add(await request.Content.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json("""
                {"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"},{"name":"score"},{"name":"avatar","decltype":"BLOB"}],
                "rows":[[9007199254740993,"Ada",1.5,{"base64":"AQID"}],[2,"Bob",0,null]],"rows_affected":0,"last_insert_rowid":null}]}
                """);
        });
        using var http = new HttpClient(handler);
        var db = Database(http);

        var rows = await db.QueryAsync("SELECT * FROM people WHERE id > ? AND active = ? AND name <> ? AND note IS ? AND avatar <> ?", [1, true, "Zed", null, TerraBaseSqlValue.FromBytes([1, 2, 3])]);
        var people = await db.QueryAsync("SELECT * FROM people WHERE name = :name AND score > @min",
            new Dictionary<string, TerraBaseSqlValue> { ["name"] = "Ada", ["min"] = 0.25 }, PersonJson.Default.Person);

        await Assert.That(bodies[0]).IsEqualTo("""{"statements":[{"sql":"SELECT * FROM people WHERE id > ? AND active = ? AND name <> ? AND note IS ? AND avatar <> ?","args":[1,true,"Zed",null,{"base64":"AQID"}]}]}""");
        await Assert.That(bodies[1]).IsEqualTo("""{"statements":[{"sql":"SELECT * FROM people WHERE name = :name AND score > @min","named":{"name":"Ada","min":0.25}}]}""");
        var first = rows[0];
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(first.Columns[0]).IsEqualTo(new TerraBaseSqlColumn("id", "INTEGER"));
        await Assert.That(first.Columns[2].Decltype).IsNull();
        await Assert.That(first.GetInt64("id")).IsEqualTo(9007199254740993L);
        await Assert.That(first.GetString(1)).IsEqualTo("Ada");
        await Assert.That(first.GetDouble("score")).IsEqualTo(1.5);
        await Assert.That(first.GetBytes("avatar")).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(rows[1].IsNull("avatar")).IsTrue();
        await Assert.That(rows[1].GetBoolean("score")).IsFalse();
        await Assert.That(() => first.GetString("id")).Throws<InvalidOperationException>();
        await Assert.That(() => first.GetBytes("name")).Throws<InvalidOperationException>();
        await Assert.That(() => first.GetOrdinal("missing")).Throws<ArgumentException>();
        await Assert.That(people[0] with { Avatar = null }).IsEqualTo(new Person(9007199254740993L, "Ada", 1.5, null));
        await Assert.That(people[0].Avatar).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(people[1]).IsEqualTo(new Person(2, "Bob", 0, null));
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

        var results = await Database(http).BatchAsync([
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
    public async Task Every_sql_error_code_throws_a_sql_exception_with_its_status_and_sqlstate()
    {
        using var codes = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("sql/error-codes.json"));
        var published = typeof(TerraBaseSqlErrorCodes).GetFields().Select(field => (string)field.GetRawConstantValue()!).Order().ToList();
        var recorded = new List<string>();
        foreach (var entry in codes.RootElement.GetProperty("codes").EnumerateArray())
        {
            var (code, sqlstate, status) = (entry.GetProperty("code").GetString()!, entry.GetProperty("sqlstate").GetString()!, entry.GetProperty("status").GetInt32());
            recorded.Add(code);
            var calls = 0;
            using var handler = new TerraBaseTestHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(TerraBaseIntegrationFixture.Json(
                    $$$"""{"error":{"code":"{{{code}}}","message":"refused","statement":1,"sqlstate":"{{{sqlstate}}}"}}""", (HttpStatusCode)status));
            });
            using var http = new HttpClient(handler);

            var error = await Assert.ThrowsAsync<TerraBaseSqlException>(() => Database(http).BatchAsync([new("SELECT 1"), new("INSERT INTO people VALUES (1)")]));

            await Assert.That(calls).IsEqualTo(code == TerraBaseSqlErrorCodes.SerializationConflict ? 3 : 1).Because(code);
            await Assert.That((int)error!.StatusCode!).IsEqualTo(status);
            await Assert.That((error.Code, error.ErrorCode, error.Sqlstate, error.Statement, error.Message)).IsEqualTo((code, code, sqlstate, 1, "refused"));
            await Assert.That(error.IsTransient).IsEqualTo(code is "serialization_conflict" or "tenant_busy");
        }

        await Assert.That(recorded.Order().ToList()).IsEquivalentTo(published);
    }

    [Test]
    public async Task A_serialization_conflict_is_resent_and_then_succeeds()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(++calls == 1
            ? TerraBaseIntegrationFixture.Json("""{"error":{"code":"serialization_conflict","message":"busy","statement":0,"sqlstate":"40001"}}""", HttpStatusCode.Conflict)
            : TerraBaseIntegrationFixture.Json("""{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":1}]}""")));
        using var http = new HttpClient(handler);

        var result = await Database(http).ExecuteAsync("INSERT INTO people (name) VALUES (?)", ["Ada"]);

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(result.RowsAffected).IsEqualTo(1);
    }

    [Test]
    public async Task Invalid_statements_are_rejected_before_any_request()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException($"request {++calls} sent"));
        using var http = new HttpClient(handler);
        var db = Database(http);

        await Assert.ThrowsAsync<ArgumentException>(() => db.BatchAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.BatchAsync([.. Enumerable.Repeat(new TerraBaseSqlStatement("SELECT 1"), 257)]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteAsync("SELECT :a", new Dictionary<string, TerraBaseSqlValue> { [":a"] = 1 }));
        await Assert.That(() => (TerraBaseSqlValue)double.NaN).Throws<ArgumentOutOfRangeException>();
        await Assert.That(calls).IsEqualTo(0);
    }
}

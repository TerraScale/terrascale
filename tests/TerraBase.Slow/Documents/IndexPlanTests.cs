using System.Net;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

/// <summary>
/// Both query compilers keep an expression index usable (docs/terrabase/turso-spike.md, planner findings): values are read
/// with <c>json_extract(doc, '$.path')</c> only, <c>IN</c> becomes equalities joined by <c>OR</c>, and a prefix becomes a
/// half-open range.
/// </summary>
public sealed class IndexPlanTests
{
    private const string Index = "tb_index_people_email";
    private static readonly string[] EmailField = ["email"];
    private static readonly string[] Emails = ["ab@x.test", "abc@x.test", "ab\u0000z@x.test", "ac@x.test", "b@x.test", "Ab@x.test", "\U0010FFFF@x.test"];

    [Test]
    public async Task Sql_equality_range_order_in_and_prefix_like_use_the_index_and_match_the_same_rows()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await SeedAsync(api);
        (string Where, string[] Expected)[] cases =
        [
            ("email = 'abc@x.test'", ["p1"]),
            // A NUL sorts before '@'.
            ("email > 'ab@x.test' AND email < 'b@x.test'", ["p1", "p3"]),
            ("email IN ('ab@x.test', 'b@x.test', 'none@x.test')", ["p0", "p4"]),
            // Case-sensitive, and the range ends before 'ac': an embedded NUL still has the prefix.
            ("email LIKE 'ab%'", ["p0", "p1", "p2"]),
            ("NOT email LIKE 'ab%' AND email < 'q'", ["p3", "p4", "p5"]),
            ("email LIKE '\U0010FFFF%'", ["p6"]),
        ];
        foreach (var (where, expected) in cases)
        {
            var sql = $"SELECT _id FROM people WHERE {where} ORDER BY _id";
            var run = await PostAsync(api, $"{database.Path}/sql", sql);
            await Assert.That(run.GetProperty("rows").EnumerateArray().Select(row => ManagementApi.DocumentName(row.GetProperty("id").GetString()!)).ToList())
                .IsEquivalentTo(expected).Because(where);
        }

        foreach (var where in new[] { "email = 'abc@x.test'", "email > 'ab@x.test' AND email < 'b@x.test'", "email IN ('a@x.test', 'b@x.test')", "email LIKE 'ab%'" })
        {
            var plan = await ExplainAsync(api, database, $"SELECT _id FROM people WHERE {where}");
            await AssertSeeksAsync(plan, where);
        }

        var sorted = await ExplainAsync(api, database, "SELECT email FROM people ORDER BY email DESC LIMIT 2");
        await Assert.That(Steps(sorted)).DoesNotContain("USE SORTER FOR ORDER BY");
        await Assert.That(Indexes(sorted)).IsEquivalentTo(["people_email"]);
    }

    [Test]
    public async Task Document_query_equality_range_in_and_prefix_seek_the_index_under_the_scan_guard()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await SeedAsync(api);
        var path = Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");
        var email = new DocumentPath(EmailField);
        DocumentFilter[] filters =
        [
            new CompareFilter(email, Comparison.Equal, Text("abc@x.test")),
            new AndFilter([new CompareFilter(email, Comparison.Greater, Text("ab")), new CompareFilter(email, Comparison.Less, Text("b"))]),
            new InFilter(email, [Text("a@x.test"), Text("b@x.test"), Text("c@x.test")]),
            new PrefixFilter(email, "ab"),
        ];
        foreach (var filter in filters)
        {
            // Every compiled scan carries the per-row budget guard (tb_qos_scan), and the index still serves the filter.
            using var command = new SqliteCommand();
            var sql = new TursoQueryCompiler(command.Parameters, RowSource.Client("people"))
                .Compile(new DocumentQuery("people", filter, [], null, 10, 1024));
            // Values are read with json_extract, never spelled ->>, and a short list is equalities, not json_each.
            await Assert.That(sql).DoesNotContain("->>");
            await Assert.That(sql).DoesNotContain("json_each");
            var plan = await QueryPlan.ExplainAsync(path, sql);
            // An IN list, rewritten to OR, is Turso's MULTI-INDEX OR over the same index.
            await Assert.That(plan.Contains($"USING INDEX {Index}", StringComparison.Ordinal)
                || plan.StartsWith($"MULTI-INDEX OR people ({Index}", StringComparison.Ordinal)).IsTrue().Because($"{filter}: {plan}");
            await Assert.That(plan).DoesNotContain("SCAN people").Because(plan);
        }
    }

    private static ScalarValue Text(string value) => new(ScalarKind.Text, value);

    private static async Task<ManagedDatabaseRef> SeedAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        // Enough rows that a full scan costs more than a seek, in one transaction.
        var people = Emails.Select((email, i) => ($"p{i}", email)).Concat(Enumerable.Range(0, 200).Select(i => ($"q{i:D3}", $"q{i}@y.test")));
        using var written = await data.PostAsJsonAsync(ManagementApi.Uri($"{database.Data}/transactions"), new
        {
            ops = people.Select(person => new { op = "put", collection = "people", id = ManagementApi.DocumentId(person.Item1), document = new { email = person.Item2 } }).ToArray(),
        });
        written.EnsureSuccessStatusCode();

        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri(database.Path + "/indexes"), new { collection = "people", fields = EmailField });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return database;
    }

    private static async Task AssertSeeksAsync(JsonElement plan, string because)
    {
        await Assert.That(Indexes(plan)).IsEquivalentTo(["people_email"]).Because(because);
        await Assert.That(Steps(plan)).DoesNotContain("SCAN people").Because(Steps(plan));
    }

    private static string Steps(JsonElement plan) => string.Join(" | ", plan.GetProperty("plan").EnumerateArray().Select(step => step.GetString()));

    private static List<string> Indexes(JsonElement plan) => [.. plan.GetProperty("indexes").EnumerateArray().Select(index => index.GetString()!)];

    // The plan route takes a project-scoped ts-auth token.
    private static async Task<JsonElement> ExplainAsync(ManagementApi api, ManagedDatabaseRef database, string sql)
    {
        using var client = api.WithToken(api.Issuer.Mint(api.User, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = "databases:read";
        }));
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{database.Name}/sql/explain"), new { sql });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PostAsync(ManagementApi api, string path, string sql)
    {
        using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(path), new { sql });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

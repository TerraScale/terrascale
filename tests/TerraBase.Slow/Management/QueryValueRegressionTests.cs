using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

// Source contract inspected at e30ffdc287530551b161b0130220be53d3c0475e:
// SqlSubset.cs, QueryService.cs, TursoStorageTransaction.Queries.cs and Primitives/DocumentJson.cs.
// Focused runner (after the locked build): nice -n 19 dotnet run --project tests/TerraBase.Slow --no-build --
// --treenode-filter '/*/*/QueryValueRegressionTests/*' --minimum-expected-tests 7
public sealed class QueryValueRegressionTests
{
    [Test]
    public async Task Document_values_round_trip_through_storage_and_SQL_without_losing_JSON_types_or_digits()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var precise = new string('9', 128);
        var body = $$"""
            {
              "nothing": null, "empty": "", "yes": true, "no": false,
              "minimum": -9223372036854775808, "maximum": 9223372036854775807,
              "precise": {{precise}}, "fraction": 0.1000000000000000000000000001,
              "small": 1e-307, "large": 1e307, "negativeZero": -0, "decimal": 1.0,
              "text": "caf\u00e9 \u6771\u4eac \ud83d\ude00 e\u0301 \u0000 ' \\ \"\n",
              "array": [null, false, 0, "", [], {}], "object": {},
              "nested": {"value": {{precise}}}, "\u6771\u4eac": "\ud83d\ude00"
            }
            """;
        using var expected = JsonDocument.Parse(body);
        await PutAsync(data, database, "sample", body);

        var read = await data.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(ManagementApi.DocumentPath(database, "query_values", "sample")));
        var all = await RunAsync(api, database, "SELECT * FROM query_values");
        var fields = expected.RootElement.EnumerateObject().Select(member => member.Name).Where(name => name != "東京").ToArray();
        var projected = await RunAsync(api, database, "SELECT " + string.Join(',', fields.Select(field => "\"" + field + "\"")) + ", nested.value FROM query_values");
        var projection = projected.GetProperty("rows")[0].GetProperty("value");

        await Assert.That(JsonElement.DeepEquals(read.GetProperty("document"), expected.RootElement)).IsTrue();
        await Assert.That(JsonElement.DeepEquals(all.GetProperty("rows")[0].GetProperty("value"), expected.RootElement)).IsTrue();
        foreach (var field in fields)
        {
            var original = expected.RootElement.GetProperty(field);
            var actual = projection.GetProperty(field);
            await Assert.That(actual.ValueKind).IsEqualTo(original.ValueKind).Because(field);
            await Assert.That(JsonElement.DeepEquals(actual, original)).IsTrue().Because(field);
            if (original.ValueKind == JsonValueKind.Number)
            {
                await Assert.That(actual.GetRawText()).IsEqualTo(original.GetRawText()).Because(field);
            }
        }

        await Assert.That(projection.GetProperty("nested.value").GetRawText()).IsEqualTo(precise);
    }

    [Test]
    public async Task SQL_distinguishes_empty_strings_and_boolean_literals_from_null_or_missing_fields()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "absent", "{}");
        await PutAsync(data, database, "null", """{"value":null,"flag":false}""");
        await PutAsync(data, database, "empty", """{"value":"","flag":true}""");
        await PutAsync(data, database, "text", """{"value":"null","flag":"true"}""");
        (string Predicate, string[] Expected)[] cases =
        [
            ("value IS NULL", ["absent", "null"]),
            ("value IS NOT NULL", ["empty", "text"]),
            ("value = ''", ["empty"]),
            ("value = 'null'", ["text"]),
            ("flag = TRUE", ["empty"]),
            ("flag = FALSE", ["null"]),
            ("flag = 'true'", ["text"]),
        ];
        foreach (var (predicate, expected) in cases)
        {
            await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values WHERE " + predicate)))
                .IsEquivalentTo(expected).Because(predicate);
        }

        var projected = await RunAsync(api, database, "SELECT value FROM query_values WHERE value IS NULL");
        foreach (var row in projected.GetProperty("rows").EnumerateArray())
        {
            await Assert.That(row.GetProperty("value").GetProperty("value").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }

    [Test]
    public async Task SQL_integer_binding_keeps_adjacent_values_distinct_at_64_bit_and_double_precision_boundaries()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        (string Id, string Number)[] numbers =
        [
            ("minimum", "-9223372036854775808"), ("next_minimum", "-9223372036854775807"),
            ("exact_double", "9007199254740992"), ("next_integer", "9007199254740993"),
            ("previous_maximum", "9223372036854775806"), ("maximum", "9223372036854775807"),
        ];
        foreach (var (id, number) in numbers)
        {
            await PutAsync(data, database, id, "{\"number\":" + number + "}");
        }

        foreach (var (id, number) in numbers)
        {
            var result = await RunAsync(api, database, "SELECT number FROM query_values WHERE number = " + number);
            await Assert.That(Ids(result)).IsEquivalentTo([id]).Because(number);
            await Assert.That(result.GetProperty("rows")[0].GetProperty("value").GetProperty("number").GetRawText())
                .IsEqualTo(number);
        }

        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values WHERE number < -9223372036854775807")))
            .IsEquivalentTo(["minimum"]);
        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values WHERE number > 9223372036854775806")))
            .IsEquivalentTo(["maximum"]);
    }

    [Test]
    public async Task SQL_strings_bind_quotes_and_Unicode_without_normalizing_or_interpreting_their_contents()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        (string Id, string Json, string Literal)[] strings =
        [
            ("composed", """{"text":"caf\u00e9"}""", "café"),
            ("decomposed", """{"text":"cafe\u0301"}""", "cafe\u0301"),
            ("astral", """{"text":"\u6771\u4eac \ud83d\ude00"}""", "東京 😀"),
            ("quoted", """{"text":"O'Brien; SELECT * FROM query_values -- \\"}""", "O'Brien; SELECT * FROM query_values -- \\"),
            ("upper", """{"text":"Café"}""", "Café"),
        ];
        foreach (var (id, json, _) in strings)
        {
            await PutAsync(data, database, id, json);
        }

        foreach (var (id, _, literal) in strings)
        {
            var sql = "SELECT text FROM query_values WHERE text = '" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
            var result = await RunAsync(api, database, sql);
            await Assert.That(Ids(result)).IsEquivalentTo([id]).Because(sql);
            await Assert.That(result.GetProperty("rows")[0].GetProperty("value").GetProperty("text").GetString()).IsEqualTo(literal);
        }

        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values WHERE text LIKE 'caf%'")))
            .IsEquivalentTo(["composed", "decomposed"]);
        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values WHERE text LIKE '東京 😀%'")))
            .IsEquivalentTo(["astral"]);
    }

    [Test]
    public async Task Empty_SQL_results_and_exact_limit_boundaries_report_only_the_rows_that_exist()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "query_values");
        var emptyCollection = await RunAsync(api, database, "SELECT * FROM query_values LIMIT 1");
        await PutAsync(data, database, "a", """{"number":1}""");
        await PutAsync(data, database, "b", """{"number":2}""");
        var emptyFilter = await RunAsync(api, database, "SELECT number FROM query_values WHERE number = 3 LIMIT 1");
        foreach (var empty in new[] { emptyCollection, emptyFilter })
        {
            await Assert.That(empty.GetProperty("rows").GetArrayLength()).IsEqualTo(0);
            await Assert.That(empty.GetProperty("matched").GetInt32()).IsEqualTo(0);
            await Assert.That(empty.GetProperty("truncated").GetBoolean()).IsFalse();
        }

        var exact = await RunAsync(api, database, "SELECT * FROM query_values ORDER BY number LIMIT 2");
        var limited = await RunAsync(api, database, "SELECT * FROM query_values ORDER BY number LIMIT 1");
        await Assert.That(Ids(exact)).IsEquivalentTo(["a", "b"]);
        await Assert.That(exact.GetProperty("matched").GetInt32()).IsEqualTo(2);
        await Assert.That(exact.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(Ids(limited)).IsEquivalentTo(["a"]);
        await Assert.That(limited.GetProperty("matched").GetInt32()).IsEqualTo(1);
        await Assert.That(limited.GetProperty("truncated").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task SQL_check_rejects_unbound_parameters_numeric_overflow_and_unsupported_shapes()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "sample", """{"number":1,"text":"literal"}""");
        (string Sql, string Error)[] cases =
        [
            ("SELECT * FROM query_values WHERE number = $number", "parameters are not supported"),
            ("SELECT * FROM query_values WHERE number = :number", "parameters are not supported"),
            ("SELECT * FROM query_values WHERE number = ?", "parameters are not supported"),
            ("SELECT * FROM query_values WHERE number = 9223372036854775808", "integers must fit in 64 bits"),
            ("SELECT * FROM query_values WHERE number = -9223372036854775809", "integers must fit in 64 bits"),
            ("SELECT * FROM query_values WHERE number = 1e309", "decimals must be finite"),
            ("SELECT * FROM query_values WHERE number = -1e309", "decimals must be finite"),
            ("SELECT * FROM query_values WHERE number IN ()", "IN takes 1 to 256 values"),
            ("SELECT * FROM query_values UNION SELECT * FROM query_values", "UNION, INTERSECT, EXCEPT, VALUES and parenthesized queries are not supported"),
            ("SELECT * FROM (SELECT * FROM query_values)", "FROM supports only a collection name, not subqueries or functions"),
            ("SELECT * FROM query_values WHERE text = 'unfinished", "SQL syntax error"),
        ];
        foreach (var (sql, error) in cases)
        {
            // /sql/check reports SQL-subset rejection as data; HTTP validation belongs to its separate owner.
            var check = await PostSqlAsync(api, database.Path + "/sql/check", sql);
            await Assert.That(check.GetProperty("ok").GetBoolean()).IsFalse().Because(sql);
            await Assert.That(check.GetProperty("error").GetString()!).StartsWith(error).Because(sql);
        }

        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values WHERE number = 1"))).IsEquivalentTo(["sample"]);
    }

    [Test]
    public async Task Cancelling_between_SQL_rows_releases_the_reader_and_allows_the_next_query()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await PutAsync(data, database, "a", """{"number":1}""");
        await PutAsync(data, database, "b", """{"number":2}""");
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var context = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        await using (var transaction = (await engine.BeginTransactionAsync(context, write: false, CancellationToken.None))!)
        {
            using var cancellation = new CancellationTokenSource();
            await using var rows = transaction.QuerySqlAsync(new SqlQuery("query_values", null, [], 100), cancellation.Token).GetAsyncEnumerator();
            await Assert.That(await rows.MoveNextAsync()).IsTrue();
            await Assert.That(ManagementApi.DocumentName(rows.Current.Id)).IsEqualTo("a");
            // Cancel at a known consumer boundary, not after a timer or during a native call.
            cancellation.Cancel();
            await Assert.That(async () => await rows.MoveNextAsync()).Throws<OperationCanceledException>();
        }

        // Turso may abort the transaction when it drains a cancelled reader (docs/terrabase/turso-spike.md).
        // Recovery uses a fresh read transaction; the cancelled transaction is only disposed.
        await using (var resumedTransaction = (await engine.BeginTransactionAsync(context, write: false, CancellationToken.None))!)
        {
            var resumed = new List<string>();
            await foreach (var row in resumedTransaction.QuerySqlAsync(new SqlQuery("query_values", null, [], 100), CancellationToken.None))
            {
                resumed.Add(ManagementApi.DocumentName(row.Id));
            }

            await Assert.That(resumed).IsEquivalentTo(["a", "b"]);
        }

        await Assert.That(Ids(await RunAsync(api, database, "SELECT * FROM query_values"))).IsEquivalentTo(["a", "b"]);
    }

    private static async Task PutAsync(HttpClient data, ManagedDatabaseRef database, string id, string json)
    {
        await ManagementApi.CreateCollectionAsync(data, database, "query_values");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await data.PutAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "query_values", id)), content);
        response.EnsureSuccessStatusCode();
    }

    private static Task<JsonElement> RunAsync(ManagementApi api, ManagedDatabaseRef database, string sql) =>
        PostSqlAsync(api, database.Path + "/sql", sql);

    private static async Task<JsonElement> PostSqlAsync(ManagementApi api, string path, string sql)
    {
        using var response = await api.Client.PostAsJsonAsync(ManagementApi.Uri(path), new { sql });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Ids(JsonElement result) =>
        [.. result.GetProperty("rows").EnumerateArray().Select(row => ManagementApi.DocumentName(row.GetProperty("id").GetString()!))];
}

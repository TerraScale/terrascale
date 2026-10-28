using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Hrana;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Hrana;

/// <summary>Hrana over HTTP with the request shapes @libsql/client (v2) and the Rust crate (v3) send.</summary>
public sealed class HranaPipelineTests
{
    internal const string CreateT = """{"requests":[{"type":"execute","stmt":{"sql":"CREATE TABLE t (x INTEGER PRIMARY KEY)","args":[],"named_args":[],"want_rows":false}},{"type":"close"}]}""";

    [Test]
    public async Task Rust_requests_run_with_stored_sql_and_values_round_trip_in_hrana_encoding()
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT);
        // The Rust crate: v3, no Content-Type, explicit nulls, ';' appended.
        var values = await PostAsync(api, """
            {"baton": null, "requests": [{"type": "store_sql", "sql_id": 1, "sql": "INSERT INTO t VALUES (?1);"},
             {"type": "execute", "stmt": {"sql": null, "sql_id": 1, "args": [{"type": "integer", "value": "1"}], "named_args": [], "want_rows": false, "replication_index": null}},
             {"type": "execute", "stmt": {"sql": "SELECT x FROM t;", "sql_id": null, "args": [], "named_args": [], "want_rows": true, "replication_index": null}},
             {"type": "execute", "stmt": {"sql": "SELECT ?, ?, ?, ?, ?;", "sql_id": null, "args": [{"type": "float", "value": 1}, {"type": "text", "value": "é \"q\""}, {"type": "null"}, {"type": "integer", "value": "9007199254740993"}, {"type": "float", "value": 2.5}], "named_args": [], "want_rows": true, "replication_index": null}},
             {"type": "close_sql", "sql_id": 1},
             {"type": "execute", "stmt": {"sql": null, "sql_id": 1, "args": [{"type": "integer", "value": "2"}], "named_args": [], "want_rows": false, "replication_index": null}},
             {"type": "execute", "stmt": {"sql": "SELECT ?;", "args": [{"type": "blob", "base64": "AQID"}]}},
             {"type": "get_autocommit"}, {"type": "close"}]}
            """, version: "v3", contentType: null);

        await Assert.That(values.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(values.ContentType).IsEqualTo("application/json");
        await Assert.That(values.Json.GetProperty("baton").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(values.Json.GetProperty("base_url").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Types(values.Json)).IsEqualTo("ok:store_sql,ok:execute,ok:execute,ok:execute,ok:close_sql,error,error,ok:get_autocommit,ok:close");
        var inserted = Response(values.Json, 1).GetProperty("result");
        await Assert.That(inserted.GetProperty("affected_row_count").GetInt64()).IsEqualTo(1);
        await Assert.That(inserted.GetProperty("last_insert_rowid").GetString()).IsEqualTo("1");
        var stored = Response(values.Json, 2).GetProperty("result");
        await Assert.That(stored.GetProperty("cols")[0].GetRawText()).IsEqualTo("""{"name":"x","decltype":"INTEGER"}""");
        await Assert.That(stored.GetProperty("rows").GetRawText()).IsEqualTo("""[[{"type":"integer","value":"1"}]]""");
        await Assert.That(stored.GetProperty("affected_row_count").GetInt64()).IsEqualTo(0);
        await Assert.That(stored.GetProperty("last_insert_rowid").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Response(values.Json, 3).GetProperty("result").GetProperty("rows")[0].GetRawText())
            .IsEqualTo("""[{"type":"float","value":1},{"type":"text","value":"é \"q\""},{"type":"null"},{"type":"integer","value":"9007199254740993"},{"type":"float","value":2.5}]""");
        await Assert.That(values.Json.GetProperty("results")[5].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQL_NOT_FOUND");
        await Assert.That(values.Json.GetProperty("results")[6].GetProperty("error").GetRawText())
            .IsEqualTo("""{"message":"blob values are not supported","code":"SQLITE_ERROR"}""");
        await Assert.That(Response(values.Json, 7).GetProperty("is_autocommit").GetBoolean()).IsTrue();
        // Relaxed escaping: the Rust client borrows strings and fails on escapes JSON does not require.
        await Assert.That(values.Raw).Contains("\"é \\\"q\\\"\"");
    }

    [Test]
    public async Task Scopes_routes_bodies_and_the_request_budget_are_enforced()
    {
        await using var api = await DocumentApi.StartAsync();
        using var reader = await api.ClientWithScopesAsync("documents:read");
        using var writer = await api.ClientWithScopesAsync("documents:write");
        using var anonymous = api.Management.DataClient();
        await PostAsync(api, CreateT);
        var read = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (9)"}},{"type":"close"}]}""", client: reader);
        var writeOnly = await PostAsync(api, CreateT, client: writer);
        var unauthenticated = await PostAsync(api, CreateT, client: anonymous);
        var version = await PostAsync(api, CreateT, version: "v4");
        var malformed = await PostAsync(api, """{"requests": 1}""");
        var large = await PostAsync(api, $$$"""{"requests":[{"type":"execute","stmt":{"sql":"SELECT '{{{new string('x', HranaRoute.MaxRequestBytes)}}}'"}}]}""");
        var unknownDatabase = await PostAsync(api, CreateT, database: "nope");
        var describes = await PostAsync(api, $$$"""{"requests":[{{{string.Join(',', Enumerable.Repeat("""{"type":"describe","sql":"SELECT 1"}""", 257))}}}]}""");
        var script = string.Join(';', Enumerable.Repeat("SELECT 1", 200));
        var longSequence = await PostAsync(api, $$$"""{"requests":[{{{string.Join(',', Enumerable.Repeat("""{"type":"execute","stmt":{"sql":"SELECT 1"}}""", 100))}}},{"type":"sequence","sql":"{{{script}}}"},{"type":"sequence","sql":"SELECT 1; SELECT 2"},{"type":"close"}]}""");
        var tooMany = await PostAsync(api, $$$"""{"requests":[{"type":"batch","batch":{"steps":[{{{string.Join(',', Enumerable.Repeat("""{"stmt":{"sql":"SELECT 1"}}""", 257))}}}]}},{"type":"close"}]}""");
        var stillEmpty = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t"}},{"type":"close"}]}""");

        var tenant = await api.TenantOfAsync();
        var admission = api.Server.Services.GetRequiredService<IQosAdmission>();
        var held = new List<IDisposable>();
        ApiResult busy;
        try
        {
            for (var i = 0; i < QosPolicy.For(PlanTier.Hobby).Concurrency; i++)
            {
                held.Add(admission.TryEnter(tenant, out _) ?? throw new InvalidOperationException("fixture admission failed"));
            }

            busy = await PostAsync(api, CreateT);
        }
        finally
        {
            held.ForEach(lease => lease.Dispose());
        }

        await Assert.That(Types(read.Json)).IsEqualTo("ok:execute,error,ok:close");
        await Assert.That(read.Json.GetProperty("results")[1].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_AUTH");
        await Assert.That(writeOnly.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(unauthenticated.Status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((version.Status, Code(version))).IsEqualTo((HttpStatusCode.NotFound, "NOT_FOUND"));
        await Assert.That((malformed.Status, Code(malformed))).IsEqualTo((HttpStatusCode.BadRequest, "PROTOCOL_ERROR"));
        await Assert.That((large.Status, Code(large))).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "payload_too_large"));
        await Assert.That((unknownDatabase.Status, Code(unknownDatabase))).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
        await Assert.That((tooMany.Status, Code(tooMany))).IsEqualTo((HttpStatusCode.BadRequest, "PROTOCOL_ERROR"));
        await Assert.That((describes.Status, Code(describes))).IsEqualTo((HttpStatusCode.BadRequest, "PROTOCOL_ERROR"));
        // 100 executes leave 156 statements; a 200-statement sequence is refused before it runs, and the allowance is spent.
        var sequences = longSequence.Json.GetProperty("results").EnumerateArray().Skip(100).ToList();
        await Assert.That(sequences[0].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(sequences[1].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(tooMany.Json.GetProperty("message").GetString()).Contains("256 statements");
        await Assert.That(Response(stillEmpty.Json, 0).GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(busy.Status).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(busy.Json.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("tenant_busy");
    }

    [Test]
    public async Task Past_the_response_bound_reads_fail_and_later_statements_never_run()
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT);
        // Each hex() doubles the text: 23 of them build one long value. Estimated as 64 + 33 for the column and 32 + 3 per character for the value:
        // 5,592,350 characters make 16,777,179 bytes, just under 16 MiB; 5,592,400 cross it.
        var crossingWrite = await PostAsync(api, """
            {"requests":[{"type":"execute","stmt":{"sql":"SELECT substr(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(char(120)))))))))))))))))))))))), 1, 5592350) AS v"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (1)"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (2)"}},{"type":"close"}]}
            """);
        var crossingRead = await PostAsync(api, """
            {"requests":[{"type":"execute","stmt":{"sql":"SELECT substr(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(char(120)))))))))))))))))))))))), 1, 5592400) AS v"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (3)"}},
             {"type":"sequence","sql":"INSERT INTO t VALUES (4)"},{"type":"close"}]}
            """);
        var rows = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t ORDER BY x"}},{"type":"close"}]}""");

        // The write whose 64-byte answer crosses the bound is refused before it commits: no success without its answer.
        await Assert.That(Types(crossingWrite.Json)).IsEqualTo("ok:execute,error,error,ok:close");
        await Assert.That(Response(crossingWrite.Json, 0).GetProperty("result").GetProperty("rows")[0][0].GetProperty("value").GetString()!.Length).IsEqualTo(5_592_350);
        await Assert.That(Codes(crossingWrite.Json, 1, 2)).IsEqualTo("SQLITE_TOOBIG,SQLITE_TOOBIG");
        await Assert.That(Types(crossingRead.Json)).IsEqualTo("error,error,error,ok:close");
        await Assert.That(Codes(crossingRead.Json, 0, 3)).IsEqualTo("SQLITE_TOOBIG,SQLITE_TOOBIG,SQLITE_TOOBIG");
        await Assert.That(Response(rows.Json, 0).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo("[]");
    }

    // 5,592,320 characters estimate 16,777,089 bytes: room for a 64-byte control, not for a one-row RETURNING (136 bytes).
    private const string NearlyFull =
        "SELECT substr(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(hex(char(120)))))))))))))))))))))))), 1, 5592320) AS v";

    [Test]
    public async Task A_returning_write_past_the_response_bound_is_refused_before_it_commits()
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT);
        var autocommit = await PostAsync(api, $$$"""
            {"requests":[{"type":"execute","stmt":{"sql":"{{{NearlyFull}}}"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (1) RETURNING x"}},{"type":"close"}]}
            """);
        var transaction = await PostAsync(api, $$$"""
            {"requests":[{"type":"execute","stmt":{"sql":"BEGIN"}},{"type":"execute","stmt":{"sql":"{{{NearlyFull}}}"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (2) RETURNING x"}},{"type":"execute","stmt":{"sql":"COMMIT"}}]}
            """);
        // The refused COMMIT left the transaction open without the refused insert: committing it now writes nothing.
        var commit = await PostAsync(api, $$$"""
            {"baton":"{{{transaction.Json.GetProperty("baton").GetString()}}}","requests":[{"type":"execute","stmt":{"sql":"COMMIT"}},{"type":"close"}]}
            """);
        var cursor = await PostAsync(api, $$$"""
            {"baton":null,"batch":{"steps":[{"stmt":{"sql":"{{{NearlyFull}}}","want_rows":true}},{"stmt":{"sql":"INSERT INTO t VALUES (3) RETURNING x","want_rows":true}}]}}
            """, version: "v3", endpoint: "cursor");
        var rows = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t ORDER BY x"}},{"type":"close"}]}""");

        await Assert.That(Types(autocommit.Json)).IsEqualTo("ok:execute,error,ok:close");
        await Assert.That(Codes(autocommit.Json, 1, 1)).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(Types(transaction.Json)).IsEqualTo("ok:execute,ok:execute,error,error");
        await Assert.That(Codes(transaction.Json, 2, 2)).IsEqualTo("SQLITE_TOOBIG,SQLITE_TOOBIG");
        await Assert.That(Types(commit.Json)).IsEqualTo("ok:execute,ok:close");
        var steps = cursor.Raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => JsonElement.Parse(line)).ToList();
        await Assert.That(string.Join(',', steps.Select(line => line.GetProperty("type").GetString()))).IsEqualTo("step_begin,row,step_end,step_error");
        await Assert.That(steps[3].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(Response(rows.Json, 0).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo("[]");
    }

    private static string Codes(JsonElement body, int skip, int take) => string.Join(',', body.GetProperty("results").EnumerateArray().Skip(skip).Take(take)
        .Select(result => result.GetProperty("error").GetProperty("code").GetString()));

    internal sealed record ApiResult(HttpStatusCode Status, JsonElement Json, string? ContentType, string Raw, string? RetryAfter = null);

    internal static async Task<ApiResult> PostAsync(
        DocumentApi api, string body, string version = "v2", string? contentType = "application/json", HttpClient? client = null, string database = DocumentApi.Database,
        string endpoint = "pipeline")
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (contentType is not null)
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        using var response = await (client ?? api.Client).PostAsync(new Uri($"{api.DatabasePath(database)}/libsql/{version}/{endpoint}", UriKind.Relative), content);
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw.Length == 0 || endpoint != "pipeline" ? "null" : raw);
        return new ApiResult(response.StatusCode, json.RootElement.Clone(), response.Content.Headers.ContentType?.ToString(), raw, response.Headers.RetryAfter?.ToString());
    }

    internal static async Task AssertExpiredAsync(ApiResult result)
    {
        await Assert.That((result.Status, Code(result))).IsEqualTo((HttpStatusCode.BadRequest, "STREAM_EXPIRED"));
        await Assert.That(result.ContentType).IsEqualTo("application/json");
        await Assert.That(result.Json.GetProperty("message").GetString()).StartsWith("stream not found");
        await Assert.That(result.Json.GetProperty("error").GetString()).StartsWith("stream not found");
    }

    internal static string? Code(ApiResult result) => result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("code", out var code) ? code.GetString() : null;

    internal static JsonElement Response(JsonElement body, int index) => body.GetProperty("results")[index].GetProperty("response");

    internal static string Types(JsonElement body) => string.Join(',', body.GetProperty("results").EnumerateArray().Select(result =>
        result.GetProperty("type").GetString() == "ok" ? "ok:" + result.GetProperty("response").GetProperty("type").GetString() : "error"));

    internal static string Nulls(JsonElement list) => string.Join(',', list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Null ? "null" : "value"));
}

using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Hrana;

/// <summary>Hrana over HTTP with the request shapes @libsql/client (v2) and the Rust crate (v3) send.</summary>
public sealed class HranaPipelineTests
{
    internal const string CreateT = """{"requests":[{"type":"execute","stmt":{"sql":"CREATE TABLE t (x INTEGER PRIMARY KEY)","args":[],"named_args":[],"want_rows":false}},{"type":"close"}]}""";

    [Test]
    public async Task Rust_requests_run_and_values_round_trip_in_hrana_encoding()
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT);
        // The Rust crate: v3, no Content-Type, explicit nulls, ';' appended.
        var values = await PostAsync(api, """
            {"baton": null, "requests": [
             {"type": "execute", "stmt": {"sql": "INSERT INTO t VALUES (?1);", "sql_id": null, "args": [{"type": "integer", "value": "1"}], "named_args": [], "want_rows": false, "replication_index": null}},
             {"type": "execute", "stmt": {"sql": "SELECT x FROM t;", "sql_id": null, "args": [], "named_args": [], "want_rows": true, "replication_index": null}},
             {"type": "execute", "stmt": {"sql": "SELECT ?, ?, ?, ?, ?;", "sql_id": null, "args": [{"type": "float", "value": 1}, {"type": "text", "value": "é \"q\""}, {"type": "null"}, {"type": "integer", "value": "9007199254740993"}, {"type": "float", "value": 2.5}], "named_args": [], "want_rows": true, "replication_index": null}},
             {"type": "execute", "stmt": {"sql": "SELECT ?;", "args": [{"type": "blob", "base64": "AQID"}]}},
             {"type": "get_autocommit"}, {"type": "close"}]}
            """, version: "v3", contentType: null);

        await Assert.That(values.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(values.ContentType).IsEqualTo("application/json");
        await Assert.That(values.Json.GetProperty("baton").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(values.Json.GetProperty("base_url").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Types(values.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,error,ok:get_autocommit,ok:close");
        var inserted = Response(values.Json, 0).GetProperty("result");
        await Assert.That(inserted.GetProperty("affected_row_count").GetInt64()).IsEqualTo(1);
        await Assert.That(inserted.GetProperty("last_insert_rowid").GetString()).IsEqualTo("1");
        var stored = Response(values.Json, 1).GetProperty("result");
        await Assert.That(stored.GetProperty("cols")[0].GetRawText()).IsEqualTo("""{"name":"x","decltype":"INTEGER"}""");
        await Assert.That(stored.GetProperty("rows").GetRawText()).IsEqualTo("""[[{"type":"integer","value":"1"}]]""");
        await Assert.That(stored.GetProperty("affected_row_count").GetInt64()).IsEqualTo(0);
        await Assert.That(stored.GetProperty("last_insert_rowid").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Response(values.Json, 2).GetProperty("result").GetProperty("rows")[0].GetRawText())
            .IsEqualTo("""[{"type":"float","value":1},{"type":"text","value":"é \"q\""},{"type":"null"},{"type":"integer","value":"9007199254740993"},{"type":"float","value":2.5}]""");
        await Assert.That(values.Json.GetProperty("results")[3].GetProperty("error").GetRawText())
            .IsEqualTo("""{"message":"blob values are not supported","code":"SQLITE_ERROR"}""");
        await Assert.That(Response(values.Json, 4).GetProperty("is_autocommit").GetBoolean()).IsTrue();
        // Relaxed escaping: the Rust client borrows strings and fails on escapes JSON does not require.
        await Assert.That(values.Raw).Contains("\"é \\\"q\\\"\"");
    }

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

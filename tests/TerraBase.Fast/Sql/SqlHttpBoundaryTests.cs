using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Hrana;

namespace TerraBase.Fast.Sql;

public sealed class SqlHttpBoundaryTests
{
    private const int BodyLimit = 1024 * 1024;
    private const string Select = "SELECT 1";
    private const string Control = """{"type":"get_autocommit"}""";
    private const string Close = """{"type":"close"}""";
    private const string Create = "CREATE TABLE boundary_rows (id INTEGER PRIMARY KEY, n INTEGER NOT NULL)";
    private const string Digits = "(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))";

    [Test]
    [Arguments("sql", false)]
    [Arguments("sql", true)]
    [Arguments("v2", false)]
    [Arguments("v2", true)]
    [Arguments("v3", false)]
    [Arguments("v3", true)]
    public async Task Exactly_one_MiB_is_accepted_and_one_extra_byte_cannot_write(string protocol, bool chunked)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Create);
        var atLimit = await SendSizedAsync(api, protocol, Insert(1), BodyLimit, chunked);
        await Assert.That(atLimit.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(atLimit.Json.GetProperty("results").GetArrayLength()).IsEqualTo(protocol == "sql" ? 1 : 2);
        await Assert.That(Affected(atLimit.Json, protocol)).IsEqualTo(1);

        var over = await SendSizedAsync(api, protocol, Insert(2), BodyLimit + 1, chunked);
        await Assert.That(over.Status).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        // Kestrel's chunked-body limit is translated by BadRequests before Hrana can shape its own refusal.
        var code = protocol == "sql" || chunked ? over.Json.GetProperty("error").GetProperty("code") : over.Json.GetProperty("code");
        await Assert.That(code.GetString()).IsEqualTo("payload_too_large");
        await AssertRowsAsync(api, "[[1,0]]");

        // Recover through the same protocol and client after the refusal.
        var recovered = await SendSizedAsync(api, protocol, Insert(3), 256, chunked);
        await Assert.That(recovered.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Affected(recovered.Json, protocol)).IsEqualTo(1);
        await AssertRowsAsync(api, "[[1,0],[3,0]]");
    }

    [Test]
    public async Task Sql_accepts_256_statements_and_preflights_257_before_any_write()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Create);
        var accepted = await SqlApi.ExecuteAsync(api, [Insert(1), .. Enumerable.Repeat(Select, 255)]);
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted.Json.GetProperty("results").GetArrayLength()).IsEqualTo(256);
        await Assert.That(Affected(accepted.Json, "sql")).IsEqualTo(1);

        var refused = await SqlApi.ExecuteAsync(api, [Insert(2), .. Enumerable.Repeat(Select, 255), Insert(4)]);
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await AssertRowsAsync(api, "[[1,0]]");
        await SqlApi.OkAsync(api, Insert(3));
        await AssertRowsAsync(api, "[[1,0],[3,0]]");
    }

    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Hrana_accepts_256_statements_and_preflights_257_before_any_write(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Create);
        var accepted = await HranaPipelineTests.PostAsync(api, Pipeline([Execute(Insert(1)), .. Enumerable.Repeat(Execute(Select), 255), Close]), version);
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(HranaPipelineTests.Types(accepted.Json)).IsEqualTo(string.Join(',', Enumerable.Repeat("ok:execute", 256).Append("ok:close")));
        await Assert.That(Affected(accepted.Json, version)).IsEqualTo(1);

        var refused = await HranaPipelineTests.PostAsync(api, Pipeline([Execute(Insert(2)), .. Enumerable.Repeat(Execute(Select), 255), Execute(Insert(4)), Close]), version);
        await Assert.That((refused.Status, HranaPipelineTests.Code(refused))).IsEqualTo((HttpStatusCode.BadRequest, "PROTOCOL_ERROR"));
        await AssertRowsAsync(api, "[[1,0]]");
        var recovered = await HranaPipelineTests.PostAsync(api, Pipeline([Execute(Insert(3)), Close]), version);
        await Assert.That(HranaPipelineTests.Types(recovered.Json)).IsEqualTo("ok:execute,ok:close");
        await AssertRowsAsync(api, "[[1,0],[3,0]]");
    }

    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Hrana_counts_statement_free_requests_at_1000_and_1001(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        var accepted = await HranaPipelineTests.PostAsync(api, Pipeline([.. Enumerable.Repeat(Control, 999), Close]), version);
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        var results = accepted.Json.GetProperty("results");
        await Assert.That(results.GetArrayLength()).IsEqualTo(1000);
        await Assert.That(results.EnumerateArray().Take(999).All(result =>
            result.GetProperty("type").GetString() == "ok"
            && result.GetProperty("response").GetProperty("type").GetString() == "get_autocommit"
            && result.GetProperty("response").GetProperty("is_autocommit").GetBoolean())).IsTrue();
        await Assert.That(HranaPipelineTests.Response(accepted.Json, 999).GetProperty("type").GetString()).IsEqualTo("close");

        var refused = await HranaPipelineTests.PostAsync(api, Pipeline([.. Enumerable.Repeat(Control, 1000), Close]), version);
        await Assert.That((refused.Status, HranaPipelineTests.Code(refused))).IsEqualTo((HttpStatusCode.BadRequest, "PROTOCOL_ERROR"));
        var recovered = await HranaPipelineTests.PostAsync(api, Pipeline([Execute(Create), Execute(Insert(3)), Close]), version);
        await Assert.That(HranaPipelineTests.Types(recovered.Json)).IsEqualTo("ok:execute,ok:execute,ok:close");
        await AssertRowsAsync(api, "[[3,0]]");
    }

    [Test]
    [Arguments("INSERT")]
    [Arguments("UPDATE")]
    [Arguments("DELETE")]
    public async Task A_statement_changes_1024_rows_but_1025_rolls_back_the_whole_request(string operation)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Create);
        if (operation != "INSERT")
        {
            await SqlApi.OkAsync(api, Seed(1024));
            await SqlApi.OkAsync(api, Insert(1025));
        }

        var accepted = await SqlApi.ExecuteAsync(api, Change(operation, 1024));
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Affected(accepted.Json, "sql")).IsEqualTo(1024);
        var changed = operation switch
        {
            "INSERT" => "id >= 10000 AND id < 11024 AND n = 0",
            "UPDATE" => "id > 0 AND id <= 1024 AND n = 1",
            _ => "id = 1025 AND n = 0",
        };
        await Assert.That(await RowCountAsync(api, changed)).IsEqualTo(operation == "DELETE" ? 1 : 1024);
        if (operation == "DELETE")
        {
            await Assert.That(await RowCountAsync(api, "id <= 1024")).IsEqualTo(0);
            await SqlApi.OkAsync(api, Seed(1024));
        }

        var before = await StateAsync(api);
        var refused = await SqlApi.ExecuteAsync(api, Insert(-1), Change(operation, 1025));
        await Assert.That(SqlApi.Error(refused)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 1));
        await Assert.That(await StateAsync(api)).IsEqualTo(before);
        await SqlApi.OkAsync(api, Insert(-2));
        await Assert.That(await RowCountAsync(api, "id < 0")).IsEqualTo(1);
    }

    [Test]
    public async Task Two_individually_valid_statements_cannot_commit_1025_changes()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Create);
        var refused = await SqlApi.ExecuteAsync(api, Seed(1024), Insert(1025));
        await Assert.That(SqlApi.Error(refused)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 1));
        await AssertRowsAsync(api, "[]");
        await SqlApi.OkAsync(api, Insert(3));
        await AssertRowsAsync(api, "[[3,0]]");
    }

    [Test]
    public async Task A_change_group_past_two_MiB_rolls_back_earlier_statements_and_rows()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Create, "CREATE TABLE boundary_bytes (id INTEGER PRIMARY KEY, payload TEXT)");
        // 65 * 32 KiB of ASCII payload alone exceeds 2 MiB, before keys, JSON framing and stored state.
        // The compact SQL body, 65 rows, each document and the total write framing fit their separate limits.
        var text = string.Concat(Enumerable.Repeat("hex(", 15)) + "'x'" + new string(')', 15);
        var insert = $"INSERT INTO boundary_bytes SELECT a.column1 + 10*b.column1, {text} FROM {Digits} a, {Digits} b WHERE a.column1 + 10*b.column1 < 65";
        var projected = await SqlApi.OkAsync(api, $"SELECT length({text})");
        await Assert.That(projected[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(32 * 1024);

        var refused = await SqlApi.ExecuteAsync(api, Insert(1), insert);
        await Assert.That(SqlApi.Error(refused)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 1));
        await Assert.That(refused.Message).IsEqualTo("a transaction writes at most 1024 rows and 2 MiB");
        await AssertRowsAsync(api, "[]");
        var rows = await SqlApi.OkAsync(api, "SELECT count(*) FROM boundary_bytes");
        await Assert.That(rows[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(0);
        await SqlApi.OkAsync(api, Insert(3), "INSERT INTO boundary_bytes VALUES (1, 'recovered')");
        await AssertRowsAsync(api, "[[3,0]]");
        var recovered = await SqlApi.OkAsync(api, "SELECT id, payload FROM boundary_bytes");
        await Assert.That(recovered[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1,\"recovered\"]]");
    }

    private static string Insert(int id) => $"INSERT INTO boundary_rows VALUES ({id}, 0)";

    private static string Seed(int count) =>
        $"INSERT INTO boundary_rows SELECT 1 + a.column1 + 10*b.column1 + 100*c.column1 + 1000*d.column1, 0 FROM {Digits} a, {Digits} b, {Digits} c, (VALUES (0),(1)) d WHERE a.column1 + 10*b.column1 + 100*c.column1 + 1000*d.column1 < {count}";

    private static string Change(string operation, int count) => operation switch
    {
        "INSERT" => Seed(count).Replace("SELECT 1 +", count == 1024 ? "SELECT 10000 +" : "SELECT 20000 +", StringComparison.Ordinal),
        "UPDATE" => $"UPDATE boundary_rows SET n = n + 1 WHERE id > 0 AND id <= {count}",
        _ => $"DELETE FROM boundary_rows WHERE id > 0 AND id <= {count}",
    };

    private static async Task<string> StateAsync(DocumentApi api) =>
        (await SqlApi.OkAsync(api, "SELECT id, n FROM boundary_rows ORDER BY id"))[0].GetProperty("rows").GetRawText();

    private static async Task AssertRowsAsync(DocumentApi api, string expected) =>
        await Assert.That(await StateAsync(api)).IsEqualTo(expected);

    private static async Task<long> RowCountAsync(DocumentApi api, string where) =>
        (await SqlApi.OkAsync(api, $"SELECT count(*) FROM boundary_rows WHERE {where}"))[0].GetProperty("rows")[0][0].GetInt64();

    private static string Execute(string sql) => "{\"type\":\"execute\",\"stmt\":{\"sql\":\"" + sql + "\"}}";

    private static string Pipeline(string[] requests) => "{\"requests\":[" + string.Join(',', requests) + "]}";

    private static long Affected(JsonElement json, string protocol) => protocol == "sql"
        ? json.GetProperty("results")[0].GetProperty("rows_affected").GetInt64()
        : HranaPipelineTests.Response(json, 0).GetProperty("result").GetProperty("affected_row_count").GetInt64();

    private static async Task<(HttpStatusCode Status, JsonElement Json)> SendSizedAsync(
        DocumentApi api, string protocol, string sql, int size, bool chunked)
    {
        var body = protocol == "sql" ? "{\"statements\":[{\"sql\":\"" + sql + "\"}]}" : Pipeline([Execute(sql), Close]);
        var bytes = Encoding.UTF8.GetBytes(body.PadRight(size));
        await Assert.That(bytes.Length).IsEqualTo(size);
        using var content = chunked ? (HttpContent)new ChunkedContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        await Assert.That(content.Headers.ContentLength).IsEqualTo(chunked ? (long?)null : size);
        var route = protocol == "sql" ? SqlApi.Route(api) : $"{api.DatabasePath()}/libsql/{protocol}/pipeline";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route, UriKind.Relative))
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TransferEncodingChunked = chunked;
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        using var response = await api.Client.SendAsync(request, token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return (response.StatusCode, json.RootElement.Clone());
    }

    // A non-seekable, unknown-length content writer forces the real HTTP/1.1 chunked read path.
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < bytes.Length; offset += 16 * 1024)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(16 * 1024, bytes.Length - offset)), cancellationToken);
            }
        }
    }
}

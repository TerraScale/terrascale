using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Hrana;

/// <summary>The Rust crate's query path: describe on a new stream, then a v3 cursor on its baton.</summary>
public sealed class HranaCursorTests
{
    [Test]
    public async Task Rust_queries_describe_then_stream_steps_through_the_cursor_on_the_same_stream()
    {
        await using var api = await DocumentApi.StartAsync();
        await HranaPipelineTests.PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"CREATE TABLE t (x INTEGER PRIMARY KEY)"}},{"type":"close"}]}""");
        var describe = await HranaPipelineTests.PostAsync(api, """
            {"baton": null, "requests": [{"type": "describe", "sql": "SELECT ?1", "sql_id": null, "replication_index": null}]}
            """, version: "v3", contentType: null);
        var opened = describe.Json.GetProperty("baton").GetString()!;
        var cursor = await HranaPipelineTests.PostAsync(api, $$$"""
            {"baton": "{{{opened}}}", "batch": {"steps": [
              {"condition": null, "stmt": {"sql": "SELECT ?1;", "sql_id": null, "args": [{"type": "integer", "value": "7"}], "named_args": [], "want_rows": true, "replication_index": null}},
              {"condition": {"type": "ok", "step": 0}, "stmt": {"sql": "INSERT INTO t VALUES (5);", "sql_id": null, "args": [], "named_args": [], "want_rows": false, "replication_index": null}},
              {"condition": {"type": "error", "step": 1}, "stmt": {"sql": "INSERT INTO t VALUES (6);", "sql_id": null, "args": [], "named_args": [], "want_rows": false, "replication_index": null}},
              {"condition": null, "stmt": {"sql": "SELECT x FROM missing;", "sql_id": null, "args": [], "named_args": [], "want_rows": true, "replication_index": null}}],
              "replication_index": null}}
            """, version: "v3", contentType: null, endpoint: "cursor");
        var lines = cursor.Raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonElement.Parse(line)).ToList();
        var next = lines[0].GetProperty("baton").GetString()!;
        var stale = await HranaPipelineTests.PostAsync(api, $$$"""{"baton": "{{{opened}}}", "requests": [{"type": "get_autocommit"}, {"type": "close"}]}""", version: "v3");
        var after = await HranaPipelineTests.PostAsync(api, $$$"""
            {"baton": "{{{next}}}", "requests": [{"type": "execute", "stmt": {"sql": "SELECT x FROM t;", "want_rows": true}}, {"type": "close"}]}
            """, version: "v3");
        var v2 = await HranaPipelineTests.PostAsync(api, """{"baton":null,"batch":{"steps":[]}}""", endpoint: "cursor");

        var description = HranaPipelineTests.Response(describe.Json, 0).GetProperty("result");
        await Assert.That(description.GetProperty("params").GetRawText()).IsEqualTo("""[{"name":"?1"}]""");
        await Assert.That(description.GetProperty("cols").GetArrayLength()).IsEqualTo(1);
        await Assert.That(description.GetProperty("is_explain").GetBoolean()).IsFalse();
        await Assert.That(description.GetProperty("is_readonly").GetBoolean()).IsTrue();
        await Assert.That(cursor.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(lines[0].GetProperty("base_url").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(next).IsNotEqualTo(opened);
        await Assert.That(string.Join(',', lines.Skip(1).Select(line => line.GetProperty("type").GetString())))
            .IsEqualTo("step_begin,row,step_end,step_begin,step_end,step_error");
        await Assert.That(lines[1].GetProperty("step").GetInt32()).IsEqualTo(0);
        await Assert.That(lines[2].GetProperty("row").GetRawText()).IsEqualTo("""[{"type":"integer","value":"7"}]""");
        await Assert.That(lines[3].GetRawText()).IsEqualTo("""{"type":"step_end","affected_row_count":0,"last_insert_rowid":null}""");
        await Assert.That(lines[4].GetProperty("step").GetInt32()).IsEqualTo(1);
        // The Rust client reads a cursor's last_insert_rowid as a number.
        await Assert.That(lines[5].GetRawText()).IsEqualTo("""{"type":"step_end","affected_row_count":1,"last_insert_rowid":5}""");
        await Assert.That(lines[6].GetProperty("step").GetInt32()).IsEqualTo(3);
        await Assert.That(lines[6].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_ERROR");
        // The describe stream held no state: its replaced baton opens a fresh stream rather than failing.
        await Assert.That(HranaPipelineTests.Response(stale.Json, 0).GetProperty("is_autocommit").GetBoolean()).IsTrue();
        await Assert.That(HranaPipelineTests.Response(after.Json, 0).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"5"}]]""");
        await Assert.That(v2.Status).IsEqualTo(HttpStatusCode.NotFound);
    }
}

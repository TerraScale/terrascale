using System.Text.Json;
using TerraBase.Fast.Documents;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

/// <summary>Batches with conditions as @libsql/client sends them, sequences and describe.</summary>
public sealed class HranaBatchTests
{
    // @libsql/client's batch(…, "write"), verbatim from a recorded run: step 2 binds a blob.
    private const string JsBatchWrite = """
        {"requests": [{"type": "store_sql", "sql_id": 0, "sql": "INSERT INTO t VALUES (1)"}, {"type": "store_sql", "sql_id": 1, "sql": "SELECT ?"}, {"type": "batch", "batch": {"steps": [{"stmt": {"sql": "BEGIN IMMEDIATE", "args": [], "named_args": [], "want_rows": false}}, {"condition": {"type": "ok", "step": 0}, "stmt": {"sql_id": 0, "args": [], "named_args": [], "want_rows": true}}, {"condition": {"type": "ok", "step": 1}, "stmt": {"sql_id": 1, "args": [{"type": "blob", "base64": "AQID"}], "named_args": [], "want_rows": true}}, {"condition": {"type": "ok", "step": 2}, "stmt": {"sql": "COMMIT", "args": [], "named_args": [], "want_rows": false}}, {"condition": {"type": "not", "cond": {"type": "ok", "step": 3}}, "stmt": {"sql": "ROLLBACK", "args": [], "named_args": [], "want_rows": false}}]}}, {"type": "close"}]}
        """;

    [Test]
    public async Task Js_batches_run_their_conditions_and_sequences_and_describe_answer()
    {
        await using var api = await DocumentApi.StartAsync();
        var sequence = await PostAsync(api, """{"requests":[{"type":"sequence","sql":"CREATE TABLE a(x); -- one; two\nCREATE TABLE b(y);"},{"type":"close"}]}""");
        await PostAsync(api, CreateT);
        var refused = await PostAsync(api, JsBatchWrite);
        var empty = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t"}},{"type":"close"}]}""");
        var committed = await PostAsync(api, JsBatchWrite.Replace("""{"type": "blob", "base64": "AQID"}""", """{"type": "integer", "value": "7"}""", StringComparison.Ordinal));
        var describe = await PostAsync(api, """{"requests":[{"type":"store_sql","sql_id":2,"sql":"SELECT x FROM t WHERE x = :a OR x = ?2"},{"type":"describe","sql_id":2},{"type":"describe","sql":"SELECT ?"},{"type":"close"}]}""");

        await Assert.That(Types(sequence.Json)).IsEqualTo("ok:sequence,ok:close");
        await Assert.That(sequence.Json.GetProperty("baton").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(sequence.Json.GetProperty("base_url").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Types(refused.Json)).IsEqualTo("ok:store_sql,ok:store_sql,ok:batch,ok:close");
        var steps = Response(refused.Json, 2).GetProperty("result");
        await Assert.That(Nulls(steps.GetProperty("step_results"))).IsEqualTo("value,value,null,null,value");
        await Assert.That(Nulls(steps.GetProperty("step_errors"))).IsEqualTo("null,null,value,null,null");
        await Assert.That(steps.GetProperty("step_errors")[2].GetProperty("code").GetString()).IsEqualTo("SQLITE_ERROR");
        await Assert.That(steps.GetProperty("step_errors")[2].GetProperty("message").GetString()).IsEqualTo("blob values are not supported");
        await Assert.That(steps.GetProperty("step_results")[1].GetProperty("affected_row_count").GetInt64()).IsEqualTo(1);
        await Assert.That(Response(empty.Json, 0).GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        var commit = Response(committed.Json, 2).GetProperty("result");
        await Assert.That(Nulls(commit.GetProperty("step_results"))).IsEqualTo("value,value,value,value,null");
        await Assert.That(commit.GetProperty("step_results")[1].GetProperty("last_insert_rowid").GetString()).IsEqualTo("1");
        await Assert.That(commit.GetProperty("step_results")[2].GetProperty("rows")[0][0].GetRawText()).IsEqualTo("""{"type":"integer","value":"7"}""");

        var described = Response(describe.Json, 1).GetProperty("result");
        await Assert.That(described.GetProperty("params").GetRawText()).IsEqualTo("""[{"name":":a"},{"name":"?2"}]""");
        await Assert.That(described.GetProperty("is_explain").GetBoolean()).IsFalse();
        await Assert.That(described.GetProperty("is_readonly").GetBoolean()).IsTrue();
        await Assert.That(Response(describe.Json, 2).GetProperty("result").GetProperty("params").GetRawText()).IsEqualTo("""[{"name":null}]""");
    }
}

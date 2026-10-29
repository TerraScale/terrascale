using System.Net;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Sql;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

public sealed class HranaIsComparisonTests
{
    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Values_describe_execute_and_survive_sequence_and_cross_request_transaction_replay(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, n INTEGER, s TEXT, v INTEGER)",
            "INSERT INTO t VALUES (1, 1, 'old', 0)");
        var matrix = System.Text.Json.JsonSerializer.Serialize(SqlIsComparisonTests.MatrixSql);
        var opened = await PostAsync(api, $$$"""
            {"requests":[
             {"type":"execute","stmt":{"sql":{{{matrix}}}}},
             {"type":"sequence","sql":"BEGIN; UPDATE t SET s = '01', v = v + 1 WHERE n IS '1';"},
             {"type":"store_sql","sql_id":1,"sql":"SELECT v, :x AS original, typeof(:x) AS kind FROM t AS a WHERE a.n IS :x AND a.s IS :x"},
             {"type":"describe","sql_id":1},
             {"type":"execute","stmt":{"sql_id":1,"named_args":[{"name":"x","value":{"type":"text","value":"01"}}]}}]}
            """, version: version);
        await Assert.That(opened.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Types(opened.Json)).IsEqualTo("ok:execute,ok:sequence,ok:store_sql,ok:describe,ok:execute");
        await Assert.That(string.Join(',', Response(opened.Json, 0).GetProperty("result").GetProperty("rows")[0]
            .EnumerateArray().Select(value => value.GetProperty("value").GetString()))).IsEqualTo(SqlIsComparisonTests.MatrixValues);
        var description = Response(opened.Json, 3).GetProperty("result");
        await Assert.That(description.GetProperty("params").GetRawText()).IsEqualTo("""[{"name":":x"}]""");
        await Assert.That(description.GetProperty("cols")[1].GetProperty("name").GetString()).IsEqualTo("original");
        await Assert.That(description.GetProperty("is_readonly").GetBoolean()).IsTrue();
        const string expected = """[[{"type":"integer","value":"1"},{"type":"text","value":"01"},{"type":"text","value":"text"}]]""";
        await Assert.That(Response(opened.Json, 4).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo(expected);

        var baton = opened.Json.GetProperty("baton").GetString();
        var resumed = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[
             {"type":"execute","stmt":{"sql_id":1,"named_args":[{"name":"x","value":{"type":"integer","value":"1"}}]}},
             {"type":"execute","stmt":{"sql_id":1,"named_args":[{"name":"x","value":{"type":"text","value":"01"}}]}},
             {"type":"execute","stmt":{"sql_id":1,"named_args":[{"name":"x","value":{"type":"null"}}]}},
             {"type":"execute","stmt":{"sql":"COMMIT"}},
             {"type":"execute","stmt":{"sql":"SELECT n IS 1, s IS '01', v FROM t"}},
             {"type":"close"}]}
            """, version: version);
        await Assert.That(Types(resumed.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:execute,ok:execute,ok:close");
        await Assert.That(Response(resumed.Json, 0).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo("[]");
        await Assert.That(Response(resumed.Json, 1).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo(expected);
        await Assert.That(Response(resumed.Json, 2).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo("[]");
        await Assert.That(Response(resumed.Json, 4).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"1"},{"type":"integer","value":"1"},{"type":"integer","value":"1"}]]""");
    }
}

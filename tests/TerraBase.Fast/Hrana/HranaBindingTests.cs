using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Sql;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

public sealed class HranaBindingTests
{
    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Prefix_identity_aliases_positions_and_repeated_names_bind_without_changing_http_sql(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        var result = await PostAsync(api, """
            {"requests":[
             {"type":"execute","stmt":{"sql":"SELECT :x,@x,$x,:x","named_args":[{"name":":x","value":{"type":"integer","value":"1"}},{"name":"@x","value":{"type":"integer","value":"2"}},{"name":"$x","value":{"type":"integer","value":"3"}}]}},
             {"type":"execute","stmt":{"sql":"SELECT :x,@x,$x","named_args":[{"name":"$x","value":{"type":"integer","value":"3"}},{"name":"x","value":{"type":"integer","value":"1"}}]}},
             {"type":"execute","stmt":{"sql":"SELECT ?, :x, @x, $x","args":[{"type":"integer","value":"1"},{"type":"integer","value":"9"}],"named_args":[{"name":"x","value":{"type":"integer","value":"2"}},{"name":"$x","value":{"type":"integer","value":"3"}}]}},
             {"type":"execute","stmt":{"sql":"SELECT ?3, ?1, ?3","args":[{"type":"integer","value":"1"},{"type":"null"},{"type":"integer","value":"3"}]}},
             {"type":"execute","stmt":{"sql":"SELECT ?1, $1","args":[{"type":"integer","value":"1"}],"named_args":[{"name":"1","value":{"type":"integer","value":"2"}}]}},
             {"type":"execute","stmt":{"sql":"SELECT ':literal', :v /* @ignored ?9 */","named_args":[{"name":"v","value":{"type":"text","value":"'); DROP TABLE t; --"}}]}},
             {"type":"execute","stmt":{"sql":"SELECT :v","named_args":[{"name":"v","value":{"type":"null"}}]}},
             {"type":"describe","sql":"SELECT :x,@x,$x,?5"},{"type":"close"}]}
            """, version: version);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Types(result.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:execute,ok:execute,ok:execute,ok:execute,ok:describe,ok:close");
        foreach (var (index, values) in new[] { (0, "1,2,3,1"), (1, "1,1,3"), (2, "1,2,2,3"), (3, "3,1,3"), (4, "1,2") })
        {
            var row = Response(result.Json, index).GetProperty("result").GetProperty("rows")[0];
            await Assert.That(string.Join(',', row.EnumerateArray().Select(value => value.GetProperty("value").GetString()))).IsEqualTo(values);
        }

        var text = Response(result.Json, 5).GetProperty("result").GetProperty("rows")[0];
        await Assert.That(text[0].GetProperty("value").GetString()).IsEqualTo(":literal");
        await Assert.That(text[1].GetProperty("value").GetString()).IsEqualTo("'); DROP TABLE t; --");
        await Assert.That(Response(result.Json, 6).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo("""[[{"type":"null"}]]""");
        await Assert.That(Response(result.Json, 7).GetProperty("result").GetProperty("params").GetRawText())
            .IsEqualTo("""[{"name":":x"},{"name":"@x"},{"name":"$x"},{"name":null},{"name":"?5"}]""");

        var http = await SqlApi.ExecuteAsync(api, "SELECT ?, :missing");
        await Assert.That(http.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(http.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[null,null]]");
    }

    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Missing_unexpected_and_duplicate_bindings_fail_before_writes_and_leave_the_stream_usable(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        var invalid = new[]
        {
            """{"sql":"SELECT ?"}""",
            """{"sql":"SELECT 1","named_args":[{"name":"typo","value":{"type":"null"}}]}""",
            """{"sql":"INSERT INTO t VALUES (?)"}""",
            """{"sql":"INSERT INTO t VALUES (1)","named_args":[{"name":"typo","value":{"type":"null"}}]}""",
            """{"sql":"SELECT :wanted","named_args":[{"name":"typo","value":{"type":"null"}}]}""",
            """{"sql":"SELECT :x","named_args":[{"name":"x","value":{"type":"null"}},{"name":"x","value":{"type":"integer","value":"1"}}]}""",
            """{"sql":"SELECT :x","named_args":[{"name":":x","value":{"type":"null"}},{"name":":x","value":{"type":"null"}}]}""",
            """{"sql":"SELECT ?","args":[{"type":"null"},{"type":"null"}]}""",
            """{"sql":"SELECT ?3","args":[{"type":"integer","value":"3"}]}""",
            """{"sql":"SELECT ?3","named_args":[{"name":"?3","value":{"type":"integer","value":"3"}}]}""",
            """{"sql":"SELECT :x","args":[{"type":"null"}],"named_args":[{"name":"@x","value":{"type":"null"}}]}""",
            """{"sql":"SELECT :x","named_args":[{"name":"x","value":{"type":"null"}},{"name":"typo","value":{"type":"null"}}]}""",
        };
        var requests = string.Join(',', invalid.Select(stmt => $$$"""{"type":"execute","stmt":{{{stmt}}}}"""));
        var result = await PostAsync(api, $$$"""
            {"requests":[{"type":"execute","stmt":{"sql":"BEGIN"}},
             {"type":"execute","stmt":{"sql":"CREATE TABLE t (id INTEGER PRIMARY KEY)"}},{{{requests}}},
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"execute","stmt":{"sql":"ROLLBACK"}},{"type":"close"}]}
            """, version: version);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        foreach (var failed in result.Json.GetProperty("results").EnumerateArray().Skip(2).Take(invalid.Length))
        {
            await Assert.That(failed.GetProperty("type").GetString()).IsEqualTo("error");
            await Assert.That(failed.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("ARGS_INVALID");
        }

        await Assert.That(Response(result.Json, invalid.Length + 2).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"0"}]]""");
        await Assert.That(Response(result.Json, invalid.Length + 3).GetProperty("type").GetString()).IsEqualTo("execute");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stored_sql_binding_errors_and_conditional_steps_work_in_batches_and_cursors(bool cursor)
    {
        await using var api = await DocumentApi.StartAsync();
        var stored = await PostAsync(api, """
            {"requests":[{"type":"store_sql","sql_id":7,"sql":"SELECT :x,@x,$x,:x"}]}
            """, version: "v3");
        var baton = stored.Json.GetProperty("baton").GetString();
        const string batch = """
            {"steps":[
             {"stmt":{"sql_id":7,"named_args":[{"name":"x","value":{"type":"integer","value":"1"}},{"name":"@x","value":{"type":"integer","value":"2"}},{"name":"$x","value":{"type":"integer","value":"3"}}]}},
             {"stmt":{"sql_id":7}},
             {"condition":{"type":"error","step":1},"stmt":{"sql":"SELECT ?3, ?1, ?3","args":[{"type":"integer","value":"1"},{"type":"null"},{"type":"integer","value":"3"}]}},
             {"condition":{"type":"ok","step":1},"stmt":{"sql":"SELECT 99"}}]}
            """;
        var body = cursor ? $$$"""{"baton":"{{{baton}}}","batch":{{{batch}}}}"""
            : $$$"""{"baton":"{{{baton}}}","requests":[{"type":"batch","batch":{{{batch}}}},{"type":"close"}]}""";
        var result = await PostAsync(api, body, version: "v3", endpoint: cursor ? "cursor" : "pipeline");
        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        JsonElement first;
        JsonElement last;
        if (cursor)
        {
            var lines = result.Raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonElement.Parse(line)).ToArray();
            await Assert.That(string.Join(',', lines.Skip(1).Select(line => line.GetProperty("type").GetString())))
                .IsEqualTo("step_begin,row,step_end,step_error,step_begin,row,step_end");
            await Assert.That(lines[4].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("ARGS_INVALID");
            first = lines[2].GetProperty("row");
            last = lines[6].GetProperty("row");
            var next = lines[0].GetProperty("baton").GetString();
            await PostAsync(api, $$$"""{"baton":"{{{next}}}","requests":[{"type":"close"}]}""", version: "v3");
        }
        else
        {
            var steps = Response(result.Json, 0).GetProperty("result");
            await Assert.That(steps.GetProperty("step_errors")[1].GetProperty("code").GetString()).IsEqualTo("ARGS_INVALID");
            await Assert.That(steps.GetProperty("step_results")[3].ValueKind).IsEqualTo(JsonValueKind.Null);
            first = steps.GetProperty("step_results")[0].GetProperty("rows")[0];
            last = steps.GetProperty("step_results")[2].GetProperty("rows")[0];
        }

        await Assert.That(string.Join(',', first.EnumerateArray().Select(value => value.GetProperty("value").GetString()))).IsEqualTo("1,2,3,1");
        await Assert.That(string.Join(',', last.EnumerateArray().Select(value => value.GetProperty("value").GetString()))).IsEqualTo("3,1,3");
    }
}

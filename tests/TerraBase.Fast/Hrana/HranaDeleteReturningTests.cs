using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Sql;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

public sealed class HranaDeleteReturningTests
{
    [Test]
    [Arguments("v2", "DELETE FROM t RETURNING * END; SELECT 1")]
    [Arguments("v3", "DELETE FROM t RETURNING * END; SELECT 1")]
    [Arguments("v2", "DELETE FROM t RETURNING id AS \"END\" /* tail */ END; SELECT 1;")]
    [Arguments("v3", "DELETE FROM t RETURNING id AS \"END\" /* tail */ END; SELECT 1;")]
    [Arguments("v2", "DELETE FROM t WHERE id = 1 RETURNING * END; SELECT 1")]
    [Arguments("v3", "DELETE FROM t WHERE id = 1 RETURNING * END; SELECT 1")]
    [Arguments("v2", "DELETE FROM t AS d RETURNING * END; SELECT 1")]
    [Arguments("v3", "DELETE FROM t AS d RETURNING * END; SELECT 1")]
    [Arguments("v2", "DELETE FROM t RETURNING *; SELECT 1 END; SELECT 2;")]
    [Arguments("v3", "DELETE FROM t RETURNING *; SELECT 1 END; SELECT 2;")]
    [Arguments("v2", "SELECT 1 END; SELECT 2")]
    [Arguments("v3", "SELECT 1 END; SELECT 2")]
    [Arguments("v2", "DELETE FROM t RETURNING *\uFFFF END; SELECT 1")]
    [Arguments("v3", "DELETE FROM t RETURNING *\uFFFF END; SELECT 1")]
    [Arguments("v2", "DELETE FROM t WHERE v = '\uFFFF' RETURNING *")]
    [Arguments("v3", "DELETE FROM t WHERE v = '\uFFFF' RETURNING *")]
    [Arguments("v2", "DELETE FROM t RETURNING * /* \uFFFF */; SELECT 1")]
    [Arguments("v3", "DELETE FROM t RETURNING * /* \uFFFF */; SELECT 1")]
    public async Task Malformed_returning_tails_are_refused_without_deleting_or_executing_a_script_prefix(string version, string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)", "INSERT INTO t VALUES (1, 'keep'), (2, 'also keep')");
        var statement = JsonSerializer.Serialize(sql);
        var script = JsonSerializer.Serialize("INSERT INTO t VALUES (3, 'prefix'); " + sql);
        var result = await PostAsync(api, $$$"""
            {"requests":[
             {"type":"execute","stmt":{"sql":{{{statement}}}}},
             {"type":"execute","stmt":{"sql":"SELECT * FROM t ORDER BY id"}},
             {"type":"sequence","sql":{{{script}}}},
             {"type":"execute","stmt":{"sql":"SELECT * FROM t ORDER BY id"}},
             {"type":"describe","sql":{{{statement}}}},
             {"type":"execute","stmt":{"sql":"DELETE FROM t RETURNING id AS \"END\", v; -- END"}},
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},{"type":"close"}]}
            """, version: version);
        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Types(result.Json)).IsEqualTo("error,ok:execute,error,ok:execute,error,ok:execute,ok:execute,ok:close");
        foreach (var index in new[] { 0, 2, 4 })
        {
            await Assert.That(result.Json.GetProperty("results")[index].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_ERROR");
            if (sql.Contains(char.MaxValue))
            {
                await Assert.That(result.Json.GetProperty("results")[index].GetProperty("error").GetProperty("message").GetString())
                    .IsEqualTo("U+FFFF is not supported in SQL text");
            }
        }

        foreach (var index in new[] { 1, 3, 5 })
        {
            await Assert.That(string.Join(',', Response(result.Json, index).GetProperty("result").GetProperty("rows").EnumerateArray()
                .OrderBy(row => row[0].GetProperty("value").GetString(), StringComparer.Ordinal).Select(row => row.GetRawText())))
                .IsEqualTo("""[{"type":"integer","value":"1"},{"type":"text","value":"keep"}],[{"type":"integer","value":"2"},{"type":"text","value":"also keep"}]""");
        }

        await Assert.That(Response(result.Json, 5).GetProperty("result").GetProperty("cols")[0].GetProperty("name").GetString()).IsEqualTo("END");
        await Assert.That(Response(result.Json, 6).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"0"}]]""");

        var sequence = await PostAsync(api, """
            {"requests":[
             {"type":"sequence","sql":"INSERT INTO t VALUES (3, 'END'); DELETE FROM t RETURNING id AS \"END\"; INSERT INTO t VALUES (4, 'after');"},
             {"type":"execute","stmt":{"sql":"SELECT * FROM t"}},{"type":"close"}]}
            """, version: version);
        await Assert.That(Types(sequence.Json)).IsEqualTo("ok:sequence,ok:execute,ok:close");
        await Assert.That(Response(sequence.Json, 1).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"4"},{"type":"text","value":"after"}]]""");
    }

    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Delete_returning_describes_executes_splits_scripts_and_replays_across_requests(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)", "INSERT INTO t VALUES (1, 'one'), (2, 'two')");
        var opened = await PostAsync(api, """
            {"requests":[
             {"type":"store_sql","sql_id":1,"sql":"DELETE FROM t RETURNING *"},
             {"type":"describe","sql_id":1},
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"execute","stmt":{"sql":"BEGIN"}},
             {"type":"execute","stmt":{"sql_id":1}},
             {"type":"get_autocommit"}]}
            """, version: version);
        await Assert.That(opened.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Types(opened.Json)).IsEqualTo("ok:store_sql,ok:describe,ok:execute,ok:execute,ok:execute,ok:get_autocommit");
        var description = Response(opened.Json, 1).GetProperty("result");
        await Assert.That(description.GetProperty("cols").GetRawText())
            .IsEqualTo("""[{"name":"id","decltype":"INTEGER"},{"name":"v","decltype":"TEXT"}]""");
        await Assert.That(description.GetProperty("is_readonly").GetBoolean()).IsFalse();
        await Assert.That(description.GetProperty("params").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Response(opened.Json, 2).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"2"}]]""");
        var deleted = Response(opened.Json, 4).GetProperty("result");
        await Assert.That(deleted.GetProperty("affected_row_count").GetInt64()).IsEqualTo(2);
        await Assert.That(string.Join(',', deleted.GetProperty("rows").EnumerateArray()
            .OrderBy(row => row[0].GetProperty("value").GetString(), StringComparer.Ordinal).Select(row => row.GetRawText())))
            .IsEqualTo("""[{"type":"integer","value":"1"},{"type":"text","value":"one"}],[{"type":"integer","value":"2"},{"type":"text","value":"two"}]""");
        await Assert.That(Response(opened.Json, 5).GetProperty("is_autocommit").GetBoolean()).IsFalse();

        // Ending the HTTP request releases the physical transaction; the next request replays the original DELETE.
        var baton = opened.Json.GetProperty("baton").GetString();
        var resumed = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"execute","stmt":{"sql":"ROLLBACK"}},
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"sequence","sql":"BEGIN; /* RETURNING */ DELETE FROM main.\"t\" -- RETURNING\n RETURNING t.id AS id, v;"},
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"get_autocommit"}]}
            """, version: version);
        await Assert.That(Types(resumed.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:sequence,ok:execute,ok:get_autocommit");
        foreach (var index in new[] { 0, 4 })
        {
            await Assert.That(Response(resumed.Json, index).GetProperty("result").GetProperty("rows").GetRawText())
                .IsEqualTo("""[[{"type":"integer","value":"0"}]]""");
        }

        await Assert.That(Response(resumed.Json, 2).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"2"}]]""");
        await Assert.That(Response(resumed.Json, 5).GetProperty("is_autocommit").GetBoolean()).IsFalse();

        // A split script's rendered DELETE must replay too, and a malformed script must execute nothing.
        baton = resumed.Json.GetProperty("baton").GetString();
        var finished = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"execute","stmt":{"sql":"COMMIT"}},
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (3, 'keep')"}},
             {"type":"sequence","sql":"INSERT INTO t VALUES (4, 'invalid'); DELETE FROM t RETURNING"},
             {"type":"execute","stmt":{"sql":"SELECT * FROM t"}}]}
            """, version: version);
        await Assert.That(Types(finished.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:execute,error,ok:execute");
        foreach (var index in new[] { 0, 2 })
        {
            await Assert.That(Response(finished.Json, index).GetProperty("result").GetProperty("rows").GetRawText())
                .IsEqualTo("""[[{"type":"integer","value":"0"}]]""");
        }

        await Assert.That(finished.Json.GetProperty("results")[4].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_ERROR");
        await Assert.That(Response(finished.Json, 5).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"3"},{"type":"text","value":"keep"}]]""");

        baton = finished.Json.GetProperty("baton").GetString();
        var closed = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[
             {"type":"sequence","sql":"DELETE FROM t RETURNING *; INSERT INTO t VALUES (4, 'after; RETURNING');"},
             {"type":"execute","stmt":{"sql":"SELECT * FROM t"}},{"type":"close"}]}
            """, version: version);
        await Assert.That(Types(closed.Json)).IsEqualTo("ok:sequence,ok:execute,ok:close");
        await Assert.That(Response(closed.Json, 1).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"4"},{"type":"text","value":"after; RETURNING"}]]""");
    }
}

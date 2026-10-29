using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;
using TerraBase.Testing;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

public sealed class HranaSqlRegressionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Describe_sees_uncommitted_schema_without_executing_or_billing_it(bool resume)
    {
        await using var api = await DocumentApi.StartAsync();
        const string start = """
            {"type":"execute","stmt":{"sql":"BEGIN"}},
            {"type":"execute","stmt":{"sql":"CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT DEFAULT 'new')"}},
            {"type":"execute","stmt":{"sql":"INSERT INTO t DEFAULT VALUES"}}
            """;
        const string describe = """
            {"type":"describe","sql":"SELECT * FROM t WHERE id = ?"},
            {"type":"describe","sql":"INSERT INTO t DEFAULT VALUES RETURNING *"},
            {"type":"describe","sql":"PRAGMA table_info(t)"},
            {"type":"get_autocommit"}
            """;
        string body;
        if (resume)
        {
            var opened = await PostAsync(api, $$$"""{"requests":[{{{start}}},{{{describe}}}]}""", version: "v3");
            await Assert.That(Types(opened.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:describe,ok:describe,ok:describe,ok:get_autocommit");
            var baton = opened.Json.GetProperty("baton").GetString();
            body = $$$"""{"baton":"{{{baton}}}","requests":[{{{describe}}}]}""";
        }
        else
        {
            body = $$$"""{"requests":[{{{start}}},{{{describe}}}]}""";
        }

        var described = await PostAsync(api, body, version: "v3");
        await Assert.That(described.Status).IsEqualTo(HttpStatusCode.OK);
        var offset = resume ? 0 : 3;
        var select = Response(described.Json, offset).GetProperty("result");
        var insert = Response(described.Json, offset + 1).GetProperty("result");
        const string columns = """[{"name":"id","decltype":"INTEGER"},{"name":"v","decltype":"TEXT"}]""";
        await Assert.That(select.GetProperty("cols").GetRawText()).IsEqualTo(columns);
        await Assert.That(select.GetProperty("params").GetRawText()).IsEqualTo("""[{"name":null}]""");
        await Assert.That(insert.GetProperty("cols").GetRawText()).IsEqualTo(columns);
        await Assert.That(insert.GetProperty("is_readonly").GetBoolean()).IsFalse();
        await Assert.That(Response(described.Json, offset + 2).GetProperty("result").GetProperty("cols").GetArrayLength()).IsEqualTo(6);
        await Assert.That(Response(described.Json, offset + 3).GetProperty("is_autocommit").GetBoolean()).IsFalse();
        // Describe must neither bill the existing row nor commit the pending insert.
        using var owner = api.Management.As(api.Tenant.User);
        var usage = await owner.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(
            $"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/usage"));
        await Assert.That(usage.GetProperty("month").GetProperty("readUnits").GetInt64()).IsEqualTo(0);
        await Assert.That(usage.GetProperty("month").GetProperty("writeUnits").GetInt64()).IsEqualTo(0);

        var current = described.Json.GetProperty("baton").GetString();
        var finished = await PostAsync(api, $$$"""
            {"baton":"{{{current}}}","requests":[
             {"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},
             {"type":"execute","stmt":{"sql":"DROP TABLE t"}},
             {"type":"describe","sql":"SELECT * FROM t"},
             {"type":"execute","stmt":{"sql":"ROLLBACK"}},
             {"type":"execute","stmt":{"sql":"SELECT * FROM t"}},{"type":"close"}]}
            """, version: "v3");
        await Assert.That(Types(finished.Json)).IsEqualTo("ok:execute,ok:execute,error,ok:execute,error,ok:close");
        await Assert.That(Response(finished.Json, 0).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"1"}]]""");
        await Assert.That(finished.Json.GetProperty("results")[2].GetProperty("error").GetProperty("message").GetString()).StartsWith("no such table");
        await Assert.That(finished.Json.GetProperty("results")[4].GetProperty("error").GetProperty("message").GetString()).StartsWith("no such table");
    }

    [Test]
    public async Task Describes_do_not_consume_the_transaction_statement_log()
    {
        await using var api = await DocumentApi.StartAsync();
        var prepares = string.Join(',', Enumerable.Repeat("""{"type":"describe","sql":"SELECT * FROM t"}""", 254));
        var opened = await PostAsync(api, $$$"""
            {"requests":[{"type":"execute","stmt":{"sql":"BEGIN"}},
             {"type":"execute","stmt":{"sql":"CREATE TABLE t (id INTEGER PRIMARY KEY)"}},{{{prepares}}}]}
            """);
        await Assert.That(opened.Json.GetProperty("results").EnumerateArray().All(result => result.GetProperty("type").GetString() == "ok")).IsTrue();
        var baton = opened.Json.GetProperty("baton").GetString();
        var finished = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[{"type":"describe","sql":"SELECT * FROM t"},
             {"type":"execute","stmt":{"sql":"INSERT INTO t DEFAULT VALUES RETURNING id"}},
             {"type":"execute","stmt":{"sql":"COMMIT"}},{"type":"close"}]}
            """);
        await Assert.That(Types(finished.Json)).IsEqualTo("ok:describe,ok:execute,ok:execute,ok:close");
        await Assert.That(Response(finished.Json, 1).GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    [Arguments("v2")]
    [Arguments("v3")]
    public async Task Nonfinite_input_and_output_fail_without_poisoning_the_open_transaction(string version)
    {
        await using var api = await DocumentApi.StartAsync();
        var result = await PostAsync(api, """
            {"requests":[
             {"type":"execute","stmt":{"sql":"CREATE TABLE t (id INTEGER PRIMARY KEY, v REAL DEFAULT 1.25)"}},
             {"type":"execute","stmt":{"sql":"SELECT 1e999"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t (v) VALUES (?)","args":[{"type":"float","value":1e999}],"want_rows":false}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t (v) VALUES (:v)","named_args":[{"name":"v","value":{"type":"float","value":-1e999}}]}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t DEFAULT VALUES RETURNING v"}},
             {"type":"execute","stmt":{"sql":"BEGIN"}},
             {"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (2, 2.5)"}},
             {"type":"execute","stmt":{"sql":"UPDATE t SET v = 1e308 * 1e308 RETURNING v"}},
             {"type":"execute","stmt":{"sql":"SELECT v FROM t ORDER BY v"}},
             {"type":"execute","stmt":{"sql":"ROLLBACK"}},
             {"type":"execute","stmt":{"sql":"SELECT v FROM t"}},{"type":"close"}]}
            """, version: version);

        await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Types(result.Json)).IsEqualTo("ok:execute,error,error,error,ok:execute,ok:execute,ok:execute,error,ok:execute,ok:execute,ok:execute,ok:close");
        foreach (var index in new[] { 1, 2, 3, 7 })
        {
            await Assert.That(result.Json.GetProperty("results")[index].GetProperty("error").GetRawText())
                .IsEqualTo("""{"message":"nonfinite numbers are not supported","code":"SQLITE_ERROR"}""");
        }

        const string one = """[[{"type":"float","value":1.25}]]""";
        await Assert.That(Response(result.Json, 4).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo(one);
        await Assert.That(Response(result.Json, 8).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"float","value":1.25}],[{"type":"float","value":2.5}]]""");
        await Assert.That(Response(result.Json, 10).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo(one);
    }
}

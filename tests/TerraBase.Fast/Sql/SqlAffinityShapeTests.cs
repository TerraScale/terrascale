using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityShapeTests
{
    [Test]
    [Arguments("SELECT count(*) FROM (SELECT ta.* FROM ta) q WHERE q.v = :x")]
    [Arguments("SELECT count(*) FROM ta JOIN tb USING(v) WHERE v = :x")]
    [Arguments("SELECT count(*) FROM ta NATURAL JOIN tb WHERE v = :x")]
    [Arguments("SELECT count(*) FROM (SELECT * FROM ta JOIN tb USING(v)) q WHERE q.v = :x")]
    [Arguments("SELECT count(*) FROM (SELECT * FROM ta NATURAL JOIN tb) q WHERE q.v = :x")]
    [Arguments("SELECT count(*) FROM (SELECT tb.* FROM ta JOIN tb USING(v)) q WHERE q.v = :x")]
    [Arguments("WITH q(k) AS (SELECT * FROM ta JOIN tb USING(v)) SELECT count(*) FROM q WHERE k = :x")]
    [Arguments("WITH q(k) AS (SELECT * FROM ta NATURAL JOIN tb) SELECT count(*) FROM q WHERE k = :x")]
    public async Task Wildcards_and_merged_join_columns_keep_declared_affinity(string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE ta (v INTEGER)", "CREATE TABLE tb (v INTEGER)",
            "INSERT INTO ta VALUES (1)", "INSERT INTO tb VALUES (1)");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }

    [Test]
    public async Task Wildcard_expansion_is_refused_at_a_small_preparation_bound_before_engine_execution()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (v INTEGER)", "INSERT INTO t VALUES (1)");
        var ctes = new List<string> { "q0 AS (SELECT v FROM t)" };
        // Only 2048 projected columns even without the fix; never construct the exponential exhaustion probe.
        for (var i = 1; i <= 11; i++)
        {
            ctes.Add($"q{i} AS (SELECT * FROM q{i - 1} a, q{i - 1} b)");
        }

        var shallow = "WITH " + string.Join(", ", ctes.Take(4)) + " SELECT count(*) FROM q3 WHERE v = :x";
        var accepted = await SqlApi.SendAsync(api, api.Client, new { sql = shallow, named = new { x = "01" } });
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK).Because(accepted.Message ?? shallow);
        await Assert.That(accepted.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
        var sql = "WITH " + string.Join(", ", ctes) + " SELECT count(*) FROM q11 WHERE v = :x";
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(SqlApi.Error(response).Sqlite).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(response.Message).IsEqualTo("SQL column metadata exceeds the preparation budget");
        var healthy = await SqlApi.OkAsync(api, "SELECT count(*) FROM t");
        await Assert.That(healthy[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }
}

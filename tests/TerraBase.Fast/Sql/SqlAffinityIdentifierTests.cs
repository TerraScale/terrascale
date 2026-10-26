using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityIdentifierTests
{
    [Test]
    [Arguments("WITH q AS(SELECT n AS \"é\",s AS \"É\" FROM t) SELECT count(*) FROM q WHERE \"É\"=:x")]
    [Arguments("WITH q AS(SELECT n AS \"é\",s AS \"É\" FROM t) SELECT count(*) FROM q WHERE \"é\"=:x AND \"É\"=:x")]
    [Arguments("WITH q(\"é\",\"É\") AS(SELECT n,s FROM t) SELECT count(*) FROM q WHERE \"É\"=:x")]
    [Arguments("WITH q AS(SELECT n AS v,s AS V FROM t) SELECT count(*) FROM Q WHERE V=:x AND \"v:1\"=:x")]
    [Arguments("SELECT count(*) FROM (SELECT n AS v FROM t) \"é\",(SELECT s AS v FROM t) \"É\" WHERE \"é\".v=:x AND \"É\".v=:x")]
    [Arguments("SELECT count(*) FROM (SELECT n AS \"é\" FROM t) a NATURAL JOIN (SELECT s AS \"É\" FROM t) b WHERE \"é\"=:x AND \"É\"=:x")]
    [Arguments("SELECT count(*) FROM (SELECT n AS \"é\",s AS \"É\" FROM t) a JOIN (SELECT n AS \"é\" FROM t) b USING(\"é\") WHERE \"É\"=:x")]
    [Arguments("SELECT count(*) FROM (SELECT n AS v FROM t) a NATURAL JOIN (SELECT n AS V FROM t) b WHERE V=:x")]
    [Arguments("SELECT count(*) FROM (SELECT A.* FROM t a) q WHERE q.N=:x")]
    [Arguments("WITH \"é\" AS(SELECT n AS v FROM t),\"É\" AS(SELECT s AS v FROM t) SELECT count(*) FROM \"É\" WHERE v=:x")]
    [Arguments("WITH \"é\" AS(SELECT n AS v FROM t),\"É\" AS(SELECT s AS v FROM t) SELECT count(*) FROM \"é\",\"É\" WHERE \"é\".v=:x AND \"É\".v=:x")]
    [Arguments("WITH \"aÉ\" AS(SELECT s AS v FROM t),\"aé\" AS(SELECT n AS v FROM t) SELECT count(*) FROM \"AÉ\" WHERE v=:x")]
    [Arguments("WITH q AS(SELECT n AS K,s AS \"K\" FROM t) SELECT count(*) FROM q WHERE \"K\"=:x AND k=:x")]
    public async Task Identifier_lookup_and_deduplication_fold_ASCII_only(string sql)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'01')");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }

    [Test]
    public async Task Declared_result_types_use_the_same_identifier_visibility()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t(n INTEGER,s TEXT)", "INSERT INTO t VALUES(1,'01')");
        var response = await SqlApi.SendAsync(api, api.Client,
            new { sql = "SELECT \"é\".n AS a,\"É\".s AS b FROM t \"é\",t \"É\" WHERE \"é\".n=:x AND \"É\".s=:x", named = new { x = "01" } },
            new { sql = "SELECT A.* FROM t a WHERE A.N=:x", named = new { x = "01" } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? "identifier result metadata");
        var results = response.Json.GetProperty("results");
        await Assert.That(results[0].GetProperty("columns").GetRawText()).IsEqualTo(
            """[{"name":"a","decltype":"INTEGER"},{"name":"b","decltype":"TEXT"}]""");
        await Assert.That(results[1].GetProperty("columns").GetRawText()).IsEqualTo(
            """[{"name":"n","decltype":"INTEGER"},{"name":"s","decltype":"TEXT"}]""");
        await Assert.That(results[0].GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"01"]]""");
        await Assert.That(results[1].GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"01"]]""");
    }

    [Test]
    public async Task Unicode_CTE_identities_are_distinct_and_registration_is_bounded()
    {
        await using var api = await DocumentApi.StartAsync();
        var ctes = new List<string>();
        // Flat constants: 128 small identities, no wildcard growth or recursive/exponential execution.
        for (var i = 0; i < 64; i++)
        {
            ctes.Add($"\"é{i}\" AS(SELECT 1 AS v)");
            ctes.Add($"\"É{i}\" AS(SELECT '01' AS v)");
        }

        var sql = "WITH " + string.Join(",", ctes.Take(4)) + " SELECT count(*) FROM \"É0\" WHERE v=:x";
        var accepted = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x = "01" } });
        await Assert.That(accepted.Status).IsEqualTo(HttpStatusCode.OK).Because(accepted.Message ?? "distinct Unicode CTE identities");
        await Assert.That(accepted.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
        ctes.Add("extra AS(SELECT 1 AS v)");
        var rejected = await SqlApi.SendAsync(api, api.Client,
            new { sql = "WITH " + string.Join(",", ctes) + " SELECT v FROM extra" });
        await Assert.That(SqlApi.Error(rejected).Sqlite).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(rejected.Message).IsEqualTo("SQL column metadata exceeds the preparation budget");
        var healthy = await SqlApi.OkAsync(api, "SELECT 1");
        await Assert.That(healthy[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");
    }
}

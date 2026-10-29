using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlAffinityAliasTests
{
    [Test]
    [Arguments("SELECT n AS k FROM t WHERE k = :x", "01", "[[1]]")]
    [Arguments("SELECT n AS s FROM t WHERE s = :x", "01", "[[1]]")]
    [Arguments("SELECT s AS n FROM t WHERE n = :x", "1", """[["01"]]""")]
    [Arguments("SELECT n AS k, typeof(:x) FROM t WHERE k = :x", "01", """[[1,"text"]]""")]
    [Arguments("SELECT +n AS k FROM t WHERE k = :x", "01", "[]")]
    [Arguments("SELECT n + 0 AS k FROM t WHERE k = :x", "01", "[]")]
    [Arguments("WITH q(v,v) AS (SELECT n,s FROM t) SELECT v, \"v:1\" FROM q WHERE v = :x AND \"v:1\" = :x", "01", """[[1,"01"]]""")]
    [Arguments("WITH q(v,V) AS (SELECT n,s FROM t) SELECT count(*) FROM q WHERE v = :x AND \"v:1\" = :x", "01", "[[1]]")]
    public async Task Projection_and_CTE_aliases_keep_SQLite_name_precedence_and_affinity(string sql, string x, string rows)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (n INTEGER, s TEXT)", "INSERT INTO t VALUES (1, '01')");
        var response = await SqlApi.SendAsync(api, api.Client, new { sql, named = new { x } });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK).Because(response.Message ?? sql);
        await Assert.That(response.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo(rows);
    }
}
